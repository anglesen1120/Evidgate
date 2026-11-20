using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.BodyCountSeam;
using static Tenancy.IntegrationTests.RegistryApi;
using World = Tenancy.IntegrationTests.RunCreationTests.World;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Every raw request-body write route of Ingestion, Orchestrator, Release, Policy and Authoring refuses a caller the
/// host resolved to a principal but no tenant with the pipeline's own 401 before a byte of the body is read, and keeps
/// the answer each route already gave an anonymous caller.
/// </summary>
/// <remarks>
/// A principal with an unreadable <c>X-Evidgate-Org</c> is published by the host but resolves no tenant, so a guard
/// that tested only the principal would let the body through. The same request with the organization header resolved
/// is the positive control: only the tenant changes, and the counter sees the read.
/// <para>
/// The authoring writes whose delegate reads <c>If-Match</c> before its body carry a well-formed tag in both the
/// anonymous and the tenant-less request, so the refusal under test is the body guard and not the precondition. The
/// guard sits inside the shared reader, after that tag: without a tag those routes answer 422 <c>/If-Match</c> first,
/// and no byte is read in that case either.
/// </para>
/// <para>
/// The AI settings save guards before its own <c>If-Match</c>, so it is also exercised with no tag at all: an
/// unresolved caller keeps the pipeline 401 there, and a resolved one reaches the route's own 422 instead.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class RawBodyTenantRefusalTests(
    PostgresTenancyFixture fixture,
    TenancyHostPool hosts) : IClassFixture<TenancyHostPool>
{
    private static readonly RefusalAnswer TooLarge =
        new(HttpStatusCode.RequestEntityTooLarge, "payload_too_large", "the request body is too large");

    [Fact]
    public async Task RawBodyWrite_MemberWithoutTenant_ReadsNoBodyByteAndReturnsPipelineUnauthenticated()
    {
        await using var world = await SeededAsync();
        var caller = TenancyApiFactory.Resolved(user: world.User);
        var client = world.Client;

        RawBodyWriteRoutes.Routes.Select(route => route.Key).Distinct().Count()
            .ShouldBe(RawBodyWriteRoutes.Routes.Length, "every route in the table is named once");

        foreach (var route in RawBodyWriteRoutes.Routes)
        {
            foreach (var (payload, content) in Payloads(route))
            {
                var refused = await SendAsync(
                    client,
                    route,
                    content,
                    caller,
                    headers => headers.TryAddWithoutValidation("X-Evidgate-Org", "not-a-uuid"));
                AssertPipelineRefusal(route, payload, refused);
            }

            var resolved = await SendAsync(
                client,
                route,
                Json(route.WellFormed),
                caller,
                headers => headers.TryAddWithoutValidation("X-Evidgate-Org", world.Org.ToString()));
            resolved.Answer.Status.ShouldNotBe(
                HttpStatusCode.Unauthorized,
                $"{route.Key}: resolving the tenant is not refused by the body guard ({resolved.Text})");
            BytesRead(resolved.Answer).ShouldBeGreaterThan(
                0,
                $"{route.Key}: the counter observes the read the same request makes once its tenant is resolved");
        }
    }

    [Fact]
    public async Task RawBodyWrite_AnonymousCaller_ReadsNoBodyByteAndKeepsItsOwnAnswer()
    {
        await using var world = await SeededAsync();
        var client = world.Client;

        foreach (var route in RawBodyWriteRoutes.Routes)
        {
            var answered = await SendAsync(client, route, Json(route.WellFormed), Anonymous, tenantHeader: null);
            var message = $"{route.Key}: {answered.Answer.Status} {answered.Text}";

            answered.Answer.Status.ShouldBe(route.Anonymous.Status, message);
            answered.Answer.ErrorCode.ShouldBe(route.Anonymous.Code, message);
            answered.Answer.ErrorMessage.ShouldBe(route.Anonymous.Message, message);
            BytesRead(answered.Answer).ShouldBe(0, $"{route.Key}: an anonymous caller is refused before its body is read");
        }
    }

    [Fact]
    public async Task RawBodyWrite_DeclaredOverCapLength_IsAnsweredBeforeAuthenticationOnlyWhereTheHostOwnsTheLimit()
    {
        await using var world = await SeededAsync();
        var caller = TenancyApiFactory.Resolved(user: world.User);
        var client = world.Client;

        var limited = RawBodyWriteRoutes.Routes.Where(route => route.DeclaredLimit is not null).ToArray();
        limited.Any(route => route.HandlerRefusesFirst).ShouldBeTrue("the table covers handler-owned refusals");
        limited.Any(route => !route.HandlerRefusesFirst).ShouldBeTrue("the table covers middleware-owned refusals");

        foreach (var route in limited)
        {
            var declared = new byte[route.DeclaredLimit!.Value + 1];

            // The middleware answers first only where the handler does not own the refusal; otherwise the guard
            // refuses the declared over-cap caller with the pipeline 401 and never reads the body either.
            var anonymous = await SendAsync(client, route, Declared(declared), Anonymous, tenantHeader: null);
            AssertAnswer(route.HandlerRefusesFirst ? route.Anonymous : TooLarge, route, anonymous);

            var tenantless = await SendAsync(
                client,
                route,
                Declared(declared),
                caller,
                headers => headers.TryAddWithoutValidation("X-Evidgate-Org", "not-a-uuid"));
            AssertAnswer(route.HandlerRefusesFirst ? RawBodyWriteRoutes.PipelineUnauthenticated : TooLarge, route, tenantless);
        }
    }

    [Fact]
    public async Task AiSettings_WithoutIfMatch_RefusesUnresolvedCallersBeforeThePrecondition()
    {
        await using var world = await SeededAsync();
        var caller = TenancyApiFactory.Resolved(user: world.User);
        var route = RawBodyWriteRoutes.Routes.Single(route => route.Key == "authoring.ai-settings")
            with
        { Preconditions = null };

        var anonymous = await SendAsync(world.Client, route, Json(route.WellFormed), Anonymous, tenantHeader: null);
        AssertPipelineRefusal(route, "no If-Match and no principal", anonymous);

        var tenantless = await SendAsync(
            world.Client,
            route,
            Json(route.WellFormed),
            caller,
            headers => headers.TryAddWithoutValidation("X-Evidgate-Org", "not-a-uuid"));
        AssertPipelineRefusal(route, "no If-Match and no resolved tenant", tenantless);

        var resolved = await SendAsync(
            world.Client,
            route,
            Json(route.WellFormed),
            caller,
            headers => headers.TryAddWithoutValidation("X-Evidgate-Org", world.Org.ToString()));
        AssertAnswer(
            new RefusalAnswer(HttpStatusCode.UnprocessableEntity, "request_invalid", "the AI settings request is not valid"),
            route,
            resolved,
            "a resolved tenant reaches the missing If-Match refusal");
    }

    private async Task<World> SeededAsync() =>
        await RunCreationTests.StartAsync(
            fixture,
            cases: 1,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [RawBodyWriteRoutes.ManifestCapSetting] =
                    RawBodyWriteRoutes.ManifestCap.ToString(CultureInfo.InvariantCulture),
            },
            services: services => services.AddSingleton<IStartupFilter>(new CountingBodyStartupFilter()),
            hosts: hosts);

    private static (string Name, HttpContent Content)[] Payloads(RawBodyRoute route) =>
    [
        ("a malformed body", Json("{")),
        ("a well-formed body", Json(route.WellFormed)),
        ("a body of undeclared length", new UndeclaredContent(route.Oversize())),
    ];

    private static async Task<BodyReply> SendAsync(
        HttpClient client,
        RawBodyRoute route,
        HttpContent content,
        Action<HttpRequestHeaders> caller,
        Action<HttpRequestHeaders>? tenantHeader)
    {
        using var request = new HttpRequestMessage(route.Method, route.Path) { Content = content };
        caller(request.Headers);
        tenantHeader?.Invoke(request.Headers);
        route.Preconditions?.Invoke(request.Headers);
        MarkBody(request.Headers);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new BodyReply(new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers), text);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static ByteArrayContent Declared(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        return content;
    }

    private static void AssertPipelineRefusal(RawBodyRoute route, string payload, BodyReply reply) =>
        AssertAnswer(RawBodyWriteRoutes.PipelineUnauthenticated, route, reply, $"{route.Key} with {payload}");

    private static void AssertAnswer(RefusalAnswer expected, RawBodyRoute route, BodyReply reply, string? label = null)
    {
        var message = $"{label ?? route.Key}: {reply.Answer.Status} {reply.Text}";

        reply.Answer.Status.ShouldBe(expected.Status, message);
        reply.Answer.ErrorCode.ShouldBe(expected.Code, message);
        reply.Answer.ErrorMessage.ShouldBe(expected.Message, message);
        BytesRead(reply.Answer).ShouldBe(0, $"{label ?? route.Key}: no body byte is read");
    }

    private static readonly Action<HttpRequestHeaders> Anonymous = _ => { };

    private sealed record BodyReply(ApiAnswer Answer, string Text);

    /// <summary>A body that declares no length, the shape a chunked upload has: the reader cannot refuse it up front.</summary>
    private sealed class UndeclaredContent(byte[] body) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var offset = 0; offset < body.Length; offset += 8192)
            {
                await stream.WriteAsync(body.AsMemory(offset, Math.Min(8192, body.Length - offset))).ConfigureAwait(false);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }
}
