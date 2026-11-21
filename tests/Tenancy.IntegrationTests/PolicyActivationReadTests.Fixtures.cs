using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class PolicyActivationReadTests
{
    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = _hosts.RealResolver(database);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>
    /// Appends a revision through the documented route; the answer carries its id and hash.
    /// <paramref name="caller"/> defaults to this world's admin, so another tenant's own admin can seed its project.
    /// </summary>
    private static async Task<ApiAnswer> CreateRevisionAsync(
        World world,
        string body,
        Guid? project = null,
        Action<HttpRequestHeaders>? caller = null)
    {
        var created = await SendRawAsync(
            world.Client,
            HttpMethod.Post,
            $"/v1/projects/{project ?? world.Project}/policies",
            caller ?? world.Admin,
            body);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created;
    }

    /// <summary>Activates <paramref name="revisionId"/> with a fresh <c>Idempotency-Key</c> (07.1.1-T4).</summary>
    private static Task<ApiAnswer> ActivateAsync(
        World world,
        Guid revisionId,
        Guid? project = null,
        Action<HttpRequestHeaders>? caller = null) =>
        SendRawAsync(
            world.Client,
            HttpMethod.Post,
            $"/v1/projects/{project ?? world.Project}/policy-activation",
            caller ?? world.Admin,
            $$"""{"policy_revision_id":"{{revisionId}}"}""",
            Guid.NewGuid().ToString("N"));

    private static async Task<ApiAnswer> SendRawAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        Action<HttpRequestHeaders> caller,
        string raw,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, url);
        caller(request.Headers);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        request.Content = new StringContent(raw, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    private static Task<ApiAnswer> GetAsync(World world, string url, Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Get, url, caller ?? world.Admin);

    private static string ActivationUrl(World world, Guid? project = null) => $"/v1/projects/{project ?? world.Project}/policy-activation";

    private static string HeadUrl(World world, Guid? project = null) => $"/v1/projects/{project ?? world.Project}/policies/head";

    /// <summary>
    /// The parts of the 00 §9.2 error envelope that must be identical between two refusals. <c>request_id</c> is
    /// per request (an echo of <c>X-Request-Id</c>), so it is the one member compared by shape only.
    /// </summary>
    private static string ProblemShape(ApiAnswer answer)
    {
        var error = answer.Body.GetProperty("error");
        var details = error.TryGetProperty("details", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetRawText()
            : "[]";

        return $"{error.GetProperty("code").GetString()}|{error.GetProperty("message").GetString()}|"
            + $"{error.GetProperty("retryable").GetBoolean()}|{details}";
    }

    private static IReadOnlyList<string> MemberNames(ApiAnswer answer) =>
        [.. answer.Body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Writes an <c>enforcing</c> pointer the API refuses to write itself (07.7.1 owns that mode), as the owner
    /// role (<see cref="TenantScope"/> on the owner connection, the <c>PolicyContractsTests</c> precedent). The
    /// revision is moved to <c>active</c> in the same seed, so the later advisory activation is the "already
    /// active" case the audit row must report as <c>unchanged</c>.
    /// </summary>
    private async Task SeedEnforcingPointerAsync(World world, Guid revisionId, Guid activatedBy, Guid? project = null)
    {
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            "update public.policy_revision set state = 'active' where id = @revision_id",
            ("revision_id", revisionId))).ShouldBe(1);
        (await owner.ExecuteAsync(
            """
            insert into public.policy_activation (id, org_id, project_id, policy_revision_id, mode, activated_by)
            values (gen_random_uuid(), @org_id, @project_id, @revision_id, 'enforcing', @activated_by)
            """,
            ("org_id", world.Org),
            ("project_id", project ?? world.Project),
            ("revision_id", revisionId),
            ("activated_by", activatedBy))).ShouldBe(1);
        await owner.CommitAsync();
    }

    /// <summary>Writes an advisory pointer through the owner role (the cross-project-pointer case).</summary>
    private async Task SeedPointerAsync(World world, Guid project, Guid revisionId)
    {
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            """
            insert into public.policy_activation (id, org_id, project_id, policy_revision_id, mode, activated_by)
            values (gen_random_uuid(), @org_id, @project_id, @revision_id, 'advisory', @activated_by)
            """,
            ("org_id", world.Org),
            ("project_id", project),
            ("revision_id", revisionId),
            ("activated_by", world.User))).ShouldBe(1);
        await owner.CommitAsync();
    }

    private async Task<IReadOnlyList<string>> ActivationAuditRowsAsync(World world) =>
        await _fixture.ColumnAsync<string>(
            world.Database,
            $"""
            select action || '|' || object_type || '|' || object_id || '|' || coalesce(metadata ->> 'outcome', '-')
                   || '|' || coalesce(metadata ->> 'previous_id', '-') || '|' || coalesce(metadata ->> 'mode', '-')
                   || '|' || encode(after_hash, 'hex') || '|' || actor_type || '|' || actor_id
              from public.audit_log
             where action = 'policy.activate' and org_id = '{world.Org}'
             order by seq
            """);

    private async Task<long> PointerCountAsync(World world, params Guid[] orgs)
    {
        var scope = orgs.Length == 0 ? [world.Org] : orgs;
        var filter = string.Join(", ", scope.Select(org => $"'{org:D}'"));

        return await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.policy_activation where org_id in ({filter})");
    }
}
