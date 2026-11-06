// The raise route's own refusals — the caller is decided before the body is parsed, the body is bounded before it is
// deserialized, and an action that cannot be attributed to an operator is refused. The command's rules, the writer and
// the audit row live in UsageQuotaRaiseTests.cs; both files are one test class.
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Evidgate.Api.OperatorIngress;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class UsageQuotaRaiseTests
{
    [Fact]
    public async Task RaiseQuotaLimit_OperatorRoute_DeclaredOverCapBody_413()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

        // The route accepts 4 KiB; a declared body past it is answered from the header, before any parse or statement.
        var oversized = "{\"reason\":\"" + new string('a', 5_000) + "\"}";

        var answer = await PostRawAsync(world, oversized);

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        answer.ErrorCode.ShouldBe("payload_too_large");
        (await world.CounterCountAsync()).ShouldBe(0L);
        (await world.AuditCountAsync(null, Action)).ShouldBe(0L);
    }

    [Fact]
    public async Task RaiseQuotaLimit_OperatorRoute_ChunkedOverCapBody_413()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

        // No Content-Length reveals this one: the cap is counted while the body is read, so a chunked body past it is
        // refused too rather than buffered and parsed.
        var oversized = "{\"reason\":\"" + new string('a', 5_000) + "\"}";

        var answer = await PostRawAsync(world, oversized, chunked: true);

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        answer.ErrorCode.ShouldBe("payload_too_large");
        (await world.CounterCountAsync()).ShouldBe(0L);
        (await world.AuditCountAsync(null, Action)).ShouldBe(0L);
    }

    [Fact]
    public async Task RaiseQuotaLimit_OperatorRoute_AdmittedButMalformedBody_400InvalidJson()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

        var answer = await PostRawAsync(world, "{ \"project_id\": ");

        answer.Status.ShouldBe(HttpStatusCode.BadRequest, "an admitted operator gets the body's own refusal, never a 500");
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().ShouldContain(detail =>
            detail.GetProperty("path").GetString() == "body"
            && detail.GetProperty("reason").GetString() == "invalid_json");
        (await world.CounterCountAsync()).ShouldBe(0L);
        (await world.AuditCountAsync(null, Action)).ShouldBe(0L);
    }

    [Fact]
    public async Task RaiseQuotaLimit_OperatorRoute_AdmittedWithoutSubject_403()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(
            _fixture.AppConnectionString(database),
            services: services => services.AddSingleton<IOperatorGate>(new SubjectlessGate()));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("quota-subjectless"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("meter"));

        // Creating the org and the project audit rows of their own, so the refused raise is measured as a delta: the
        // gate's principal has no id, the action has no actor to be attributed to, and nothing may be written for it.
        var auditBefore = await OrgAuditCountAsync(database, org);
        var answer = await client.SendAsync(
            HttpMethod.Post,
            $"/internal/operator/billing/orgs/{org:D}/quota-raise",
            _ => { },
            new { project_id = project, period = Period, metric = "results", new_limit = 30m, reason = Reason });

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("forbidden");
        (await _fixture.ScalarAsync<long>(
            database, $"select count(*) from public.quota_counter where org_id = '{org:D}'")).ShouldBe(0L);
        (await OrgAuditCountAsync(database, org)).ShouldBe(auditBefore, "a refused raise writes no audit row of its own");
    }

    /// <summary>The org's audit rows of every action: the baseline a refused request must not grow.</summary>
    /// <param name="database">The shared database.</param>
    /// <param name="org">The tenant.</param>
    private async Task<long> OrgAuditCountAsync(string database, Guid org) =>
        await _fixture.ScalarAsync<long>(
            database, $"select count(*) from public.audit_log where org_id = '{org:D}'").ConfigureAwait(false);

    /// <summary>Posts a body of the test's own making, optionally chunked so no header declares its size.</summary>
    /// <param name="world">The world whose operator route answers.</param>
    /// <param name="json">The body.</param>
    /// <param name="chunked">True sends it chunked, which no declared length bounds.</param>
    private static async Task<ApiAnswer> PostRawAsync(UsageQuotaWorld world, string json, bool chunked = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, world.RaiseUrl())
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (chunked)
        {
            request.Headers.TransferEncodingChunked = true;
        }

        using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>A gate that admits a principal carrying no user id: an action nobody can be held to.</summary>
    private sealed class SubjectlessGate : IOperatorGate
    {
        /// <inheritdoc />
        public ValueTask<bool> IsOperatorAsync(HttpContext httpContext)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "operator-test"));

            return ValueTask.FromResult(true);
        }
    }
}
