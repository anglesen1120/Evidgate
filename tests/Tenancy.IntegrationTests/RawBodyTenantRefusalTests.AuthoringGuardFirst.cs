using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.BodyCountSeam;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The Authoring write delegates that parse <c>If-Match</c> or the import query refuse an unauthenticated or
/// tenant-less caller with the pipeline's 401 before that header or query is validated, and read no body byte.
/// </summary>
/// <remarks>
/// Thirteen delegates: the nine body routes of the table plus the three restore routes (no body) and the import. The
/// same request from a caller whose tenant resolves keeps the answer it always had: 422 naming <c>/If-Match</c>
/// (<c>required</c> or <c>invalid</c>), 422 for a missing or unreadable import <c>format</c> or <c>dry_run</c>. A
/// well-formed request from that caller is the read-positive control: the counter sees the body, or, on a restore route,
/// the answer is not the guard's 401. Stale-version (412) and success answers per route stay in the existing
/// Authoring classes, which are not edited.
/// </remarks>
public sealed partial class RawBodyTenantRefusalTests
{
    private static readonly string[] IfMatchBodyKeys =
    [
        "authoring.node-update", "authoring.node-reorder", "authoring.node-delete",
        "authoring.checklist-update", "authoring.checklist-delete",
        "authoring.manual-case-patch", "authoring.manual-case-delete",
        "authoring.manual-case-state", "authoring.manual-case-item",
    ];

    private static readonly (string Key, string From)[] RestoreRoutes =
    [
        ("authoring.node-restore", "authoring.node-update"),
        ("authoring.checklist-restore", "authoring.checklist-update"),
        ("authoring.manual-case-restore", "authoring.manual-case-patch"),
    ];

    private static readonly Action<HttpRequestHeaders> NoTag = _ => { };

    private static readonly Action<HttpRequestHeaders> MalformedTag =
        headers => headers.TryAddWithoutValidation("If-Match", "\"not-a-version\"");

    /// <summary>The thirteen delegates with a caller-visible If-Match or query, each paired with its valid precondition.</summary>
    private static IEnumerable<RawBodyRoute> GuardFirstRoutes()
    {
        foreach (var key in IfMatchBodyKeys)
        {
            yield return RawBodyWriteRoutes.Routes.Single(route => route.Key == key);
        }

        foreach (var (key, from) in RestoreRoutes)
        {
            var source = RawBodyWriteRoutes.Routes.Single(route => route.Key == from);
            yield return source with { Key = key, Method = HttpMethod.Post, Path = source.Path + "/restore", WellFormed = "{}" };
        }

        var import = RawBodyWriteRoutes.Routes.Single(route => route.Key == "authoring.import");
        yield return import with { Preconditions = null };
    }

    private static RawBodyRoute WithoutQuery(RawBodyRoute route) =>
        route with { Path = route.Path.Split('?')[0] };

    private static RawBodyRoute WithTag(RawBodyRoute route, Action<HttpRequestHeaders> tag) =>
        route with { Preconditions = tag };

    private static bool IsImport(RawBodyRoute route) => route.Key == "authoring.import";

    /// <summary>Every precondition an unresolved caller can send: none, a malformed one, and the valid one the route needs.</summary>
    private static IEnumerable<(string Name, RawBodyRoute Route)> Variants(RawBodyRoute route)
    {
        if (IsImport(route))
        {
            yield return ("no query", WithoutQuery(route));
            yield return ("an unreadable format", route with { Path = WithoutQuery(route).Path + "?format=nope" });
            yield return ("a bad dry_run", route with { Path = route.Path + "&dry_run=maybe" });
            yield return ("a valid query", route);
            yield break;
        }

        var valid = RawBodyWriteRoutes.Routes.SingleOrDefault(candidate => candidate.Key == route.Key)?.Preconditions
            ?? RawBodyWriteRoutes.Routes.Single(candidate => candidate.Key == RestoreSource(route.Key)).Preconditions;

        yield return ("no If-Match", WithTag(route, NoTag));
        yield return ("a malformed If-Match", WithTag(route, MalformedTag));
        yield return ("a valid If-Match", WithTag(route, valid!));
    }

    private static string RestoreSource(string key) => RestoreRoutes.Single(restore => restore.Key == key).From;

    [Fact]
    public async Task AuthoringGuardFirst_AnonymousCaller_GetsPipelineUnauthenticatedBeforeIfMatchOrQuery()
    {
        await using var world = await SeededAsync();

        foreach (var route in GuardFirstRoutes())
        {
            foreach (var (name, variant) in Variants(route))
            {
                var reply = await SendAsync(world.Client, variant, Json(route.WellFormed), Anonymous, tenantHeader: null);
                AssertAnswer(RawBodyWriteRoutes.PipelineUnauthenticated, variant, reply, $"{route.Key} anonymous with {name}");
            }
        }
    }

    [Fact]
    public async Task AuthoringGuardFirst_PrincipalWithoutTenant_GetsPipelineUnauthenticatedBeforeIfMatchOrQuery()
    {
        await using var world = await SeededAsync();
        var caller = TenancyApiFactory.Resolved(user: world.User);

        foreach (var route in GuardFirstRoutes())
        {
            foreach (var (name, variant) in Variants(route))
            {
                var reply = await SendAsync(
                    world.Client,
                    variant,
                    Json(route.WellFormed),
                    caller,
                    headers => headers.TryAddWithoutValidation("X-Evidgate-Org", "not-a-uuid"));
                AssertAnswer(RawBodyWriteRoutes.PipelineUnauthenticated, variant, reply, $"{route.Key} without tenant, {name}");
            }
        }
    }

    [Fact]
    public async Task AuthoringGuardFirst_ResolvedCaller_KeepsItsPreconditionAnswers()
    {
        await using var world = await SeededAsync();
        var caller = TenancyApiFactory.Resolved(user: world.User);
        void Tenant(HttpRequestHeaders headers) => headers.TryAddWithoutValidation("X-Evidgate-Org", world.Org.ToString());

        foreach (var route in GuardFirstRoutes().Where(route => !IsImport(route)))
        {
            var missing = await SendAsync(world.Client, WithTag(route, NoTag), Json(route.WellFormed), caller, Tenant);
            AssertIfMatch422(route, "required", missing);

            var malformed = await SendAsync(world.Client, WithTag(route, MalformedTag), Json(route.WellFormed), caller, Tenant);
            AssertIfMatch422(route, "invalid", malformed);
        }

        var import = GuardFirstRoutes().Single(IsImport);
        foreach (var (name, variant) in Variants(import).Take(3))
        {
            var reply = await SendAsync(world.Client, variant, Json(import.WellFormed), caller, Tenant);
            reply.Answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"import with {name}: {reply.Text}");
            BytesRead(reply.Answer).ShouldBe(0, $"import with {name}: the query is refused before the file is read");
        }
    }

    [Fact]
    public async Task AuthoringGuardFirst_ResolvedCaller_ReadPositiveControlPerEndpointFile()
    {
        await using var world = await SeededAsync();
        var caller = TenancyApiFactory.Resolved(user: world.User);
        void Tenant(HttpRequestHeaders headers) => headers.TryAddWithoutValidation("X-Evidgate-Org", world.Org.ToString());

        foreach (var route in GuardFirstRoutes())
        {
            var valid = Variants(route).Last().Route;
            var reply = await SendAsync(world.Client, valid, Json(route.WellFormed), caller, Tenant);
            var message = $"{route.Key}: {reply.Answer.Status} {reply.Text}";

            reply.Answer.Status.ShouldNotBe(HttpStatusCode.Unauthorized, message);
            if (!RestoreRoutes.Any(restore => restore.Key == route.Key))
            {
                BytesRead(reply.Answer).ShouldBeGreaterThan(0, $"{route.Key}: the counter observes the read once the caller is resolved");
            }
        }
    }

    private static void AssertIfMatch422(RawBodyRoute route, string reason, BodyReply reply)
    {
        var message = $"{route.Key} ({reason}): {reply.Answer.Status} {reply.Text}";

        reply.Answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, message);
        reply.Answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .ShouldContain(
                detail => detail.GetProperty("path").GetString() == "/If-Match" && detail.GetProperty("reason").GetString() == reason,
                message);
        BytesRead(reply.Answer).ShouldBe(0, $"{route.Key}: the precondition is refused before any body byte is read");
    }
}
