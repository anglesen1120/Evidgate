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
/// Thirteen delegates: the nine body routes of the table plus the three restore routes (no body) and the import. On
/// those routes a caller whose tenant resolves answers 422 naming <c>/If-Match</c> (<c>required</c> or
/// <c>invalid</c>), and 422 for a missing or unreadable import <c>format</c> or <c>dry_run</c>. A well-formed request
/// from that caller is the read-positive control: the counter sees the body, or, on a restore route, the answer is not
/// the guard's 401. Stale-version (412) and success answers per route stay in the existing Authoring classes, which
/// are not edited. The three guard-only routes pin only that a resolved caller is not refused with that 401.
/// Sixteen write rows in all: those thirteen, plus change discard,
/// manual-case unlink and version restore, which parse no request data. Those three are regression pins — they
/// answer the pipeline 401 on the base and on this head — while <c>AuthoringGuardFirstBoundaryTests</c> holds the
/// guard boundary for every registered write route.
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

    /// <summary>
    /// The three Authoring write routes that parse no request data: change discard, manual-case unlink and version
    /// restore. Their rows are regression pins, so the guard sweep must leave their answers as they were on the base.
    /// </summary>
    private static IEnumerable<RawBodyRoute> GuardOnlyRoutes()
    {
        var authoring = RawBodyWriteRoutes.Routes.Single(route => route.Key == "authoring.node-create").Path[..^"/nodes".Length];

        yield return new RawBodyRoute(
            "authoring.change-discard",
            HttpMethod.Post,
            $"{authoring}/changes/{Guid.NewGuid():D}/discard",
            64 * 1024,
            RawBodyWriteRoutes.PipelineUnauthenticated,
            "{}");
        yield return new RawBodyRoute(
            "authoring.manual-case-unlink",
            HttpMethod.Delete,
            $"{authoring}/manual-cases/{Guid.NewGuid():D}/links/{Guid.NewGuid():D}",
            64 * 1024,
            RawBodyWriteRoutes.PipelineUnauthenticated,
            "{}");
        yield return new RawBodyRoute(
            "authoring.version-restore",
            HttpMethod.Post,
            $"{authoring}/versions/{Guid.NewGuid():D}/restore",
            64 * 1024,
            RawBodyWriteRoutes.PipelineUnauthenticated,
            "{}");
    }

    [Fact]
    public async Task AuthoringGuardFirst_AnonymousCaller_GetsPipelineUnauthenticatedBeforeIfMatchOrQuery()
    {
        await using var world = await SeededAsync();
        var failures = new List<string>();

        foreach (var route in GuardFirstRoutes())
        {
            foreach (var (name, variant) in Variants(route))
            {
                var reply = await SendAsync(world.Client, variant, Json(route.WellFormed), Anonymous, tenantHeader: null);
                Collect(failures, $"{route.Key} anonymous with {name}", () =>
                    AssertAnswer(RawBodyWriteRoutes.PipelineUnauthenticated, variant, reply, $"{route.Key} anonymous with {name}"));
            }
        }

        failures.ShouldBeEmpty(string.Join("\n", failures));
    }

    [Fact]
    public async Task AuthoringGuardFirst_PrincipalWithoutTenant_GetsPipelineUnauthenticatedBeforeIfMatchOrQuery()
    {
        await using var world = await SeededAsync();
        var failures = new List<string>();
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
                Collect(failures, $"{route.Key} without tenant, {name}", () =>
                    AssertAnswer(RawBodyWriteRoutes.PipelineUnauthenticated, variant, reply, $"{route.Key} without tenant, {name}"));
            }
        }

        failures.ShouldBeEmpty(string.Join("\n", failures));
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
            reply.Answer.ErrorCode.ShouldBe("request_invalid", $"import with {name}");
            var member = name == "a bad dry_run" ? "/dry_run" : "/format";
            reply.Answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
                .ShouldContain(detail => detail.GetProperty("path").GetString() == member, $"import with {name}: the 422 names {member}");
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

    [Fact]
    public async Task AuthoringGuardOnlyWrites_AnonymousCaller_GetsPipelineUnauthenticated()
    {
        await using var world = await SeededAsync();
        var failures = new List<string>();

        foreach (var route in GuardOnlyRoutes())
        {
            var reply = await SendAsync(world.Client, route, Json(route.WellFormed), Anonymous, tenantHeader: null);
            Collect(failures, $"{route.Key} anonymous", () =>
                AssertAnswer(RawBodyWriteRoutes.PipelineUnauthenticated, route, reply, $"{route.Key} anonymous"));
        }

        failures.ShouldBeEmpty(string.Join("\n", failures));
    }

    [Fact]
    public async Task AuthoringGuardOnlyWrites_PrincipalWithoutTenant_GetsPipelineUnauthenticated()
    {
        await using var world = await SeededAsync();
        var failures = new List<string>();
        var caller = TenancyApiFactory.Resolved(user: world.User);

        foreach (var route in GuardOnlyRoutes())
        {
            var reply = await SendAsync(
                world.Client,
                route,
                Json(route.WellFormed),
                caller,
                headers => headers.TryAddWithoutValidation("X-Evidgate-Org", "not-a-uuid"));
            Collect(failures, $"{route.Key} without tenant", () =>
                AssertAnswer(RawBodyWriteRoutes.PipelineUnauthenticated, route, reply, $"{route.Key} without tenant"));
        }

        failures.ShouldBeEmpty(string.Join("\n", failures));
    }

    /// <summary>
    /// A resolved caller is not refused by the guard on a write route that parses no request data; no other property
    /// of that answer is pinned here.
    /// </summary>
    [Fact]
    public async Task AuthoringGuardOnlyWrites_ResolvedCaller_PassesTheGuard()
    {
        await using var world = await SeededAsync();
        var caller = TenancyApiFactory.Resolved(user: world.User);
        var failures = new List<string>();
        void Tenant(HttpRequestHeaders headers) => headers.TryAddWithoutValidation("X-Evidgate-Org", world.Org.ToString());

        foreach (var route in GuardOnlyRoutes())
        {
            var reply = await SendAsync(world.Client, route, Json(route.WellFormed), caller, Tenant);
            Collect(failures, $"{route.Key} resolved", () =>
                reply.Answer.Status.ShouldNotBe(HttpStatusCode.Unauthorized, $"{route.Key}: {reply.Text}"));
        }

        failures.ShouldBeEmpty(string.Join("\n", failures));
    }

    /// <summary>Runs one assertion and records its failure, so a single run reports every route that still answers the old order.</summary>
    private static void Collect(List<string> failures, string label, Action assertion)
    {
        try
        {
            assertion();
        }
        catch (ShouldAssertException exception)
        {
            failures.Add($"{label}: {exception.Message.ReplaceLineEndings(" ")}");
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
