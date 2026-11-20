using System.Net;
using System.Net.Http.Headers;

namespace Tenancy.IntegrationTests;

/// <summary>The answer a route gives a caller it refuses before reading the body.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Code">The 00 §9.2 <c>error.code</c>.</param>
/// <param name="Message">The 00 §9.2 <c>error.message</c>.</param>
internal sealed record RefusalAnswer(HttpStatusCode Status, string Code, string Message);

/// <summary>
/// One raw-body write route: its path, the byte cap its reader enforces, and the answer it gives an anonymous
/// caller or a caller whose tenant the host did not resolve.
/// </summary>
/// <param name="Key">The stable name the assertions report.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The route, with placeholder ids the refusals never look up.</param>
/// <param name="CapBytes">The cap the route's own reader applies to the body.</param>
/// <param name="Anonymous">The answer the route gives a caller with no principal at all.</param>
/// <param name="WellFormed">A body the route's reader can parse; its field validation is never reached.</param>
/// <param name="Preconditions">Headers the route parses before its body read (an <c>If-Match</c>), so a test keeps
/// the only variable under test the tenant header.</param>
/// <param name="DeclaredLimit">The limit the host's body-limit middleware enforces for this route, when it has one.</param>
/// <param name="HandlerRefusesFirst">True when the handler's own refusal answers before the middleware does.</param>
internal sealed record RawBodyRoute(
    string Key,
    HttpMethod Method,
    string Path,
    int CapBytes,
    RefusalAnswer Anonymous,
    string WellFormed,
    Action<HttpRequestHeaders>? Preconditions = null,
    int? DeclaredLimit = null,
    bool HandlerRefusesFirst = false)
{
    /// <summary>The largest body the undeclared-length payload sends, so a refusal that reads nothing cannot stall it.</summary>
    internal const int MaxUndeclaredBytes = 192 * 1024;

    /// <summary>
    /// A body of undeclared length that is one byte past the route's own cap, so only a reader that counts while it
    /// reads can refuse it. Bounded at <see cref="MaxUndeclaredBytes"/>: the batch (1 MiB) and import (5 MiB) caps
    /// above that bound are pinned by the declared-length test instead, since their handler owns the refusal.
    /// </summary>
    public byte[] Oversize() =>
        Enumerable.Repeat((byte)'{', Math.Min(CapBytes, MaxUndeclaredBytes - 1) + 1).ToArray();
}

/// <summary>
/// Every raw request-body write route of the Ingestion, Orchestrator, Release, Policy and Authoring modules, with
/// the answer each one keeps for an anonymous caller.
/// </summary>
/// <remarks>
/// The caps are the constants the routes read: each module's own handler cap, or the <c>x-max-bytes</c> of the
/// contract schema the route declares. The manifest route's cap is the value this suite's counting host configures
/// (<see cref="ManifestCap"/>) so that an over-cap body stays small; its published default is 32 MiB.
/// </remarks>
internal static class RawBodyWriteRoutes
{
    /// <summary>The manifest cap the counting host configures, so its over-cap body is 64 KiB + 1.</summary>
    internal const int ManifestCap = 64 * 1024;

    /// <summary>The configuration key that lowers the manifest cap on that host.</summary>
    internal const string ManifestCapSetting = "EVIDGATE:Ingestion:MaxManifestBytes";

    /// <summary>The pipeline's own 401, rendered by the envelope middleware for <c>TenantContextMissingException</c>.</summary>
    internal static readonly RefusalAnswer PipelineUnauthenticated =
        new(HttpStatusCode.Unauthorized, "unauthenticated", "the request is not authenticated");

    private static readonly RefusalAnswer Unauthenticated =
        new(HttpStatusCode.Unauthorized, "unauthenticated", "authentication is required");

    private static readonly RefusalAnswer ProducerUnauthenticated =
        new(HttpStatusCode.Unauthorized, "unauthenticated", "no authenticated producer could be resolved");

    private static readonly Guid Project = Guid.Parse("018f0000-0000-7000-8000-00000000a001");
    private static readonly Guid Run = Guid.Parse("018f0000-0000-7000-8000-00000000a002");
    private static readonly Guid Release = Guid.Parse("018f0000-0000-7000-8000-00000000a003");
    private static readonly Guid Node = Guid.Parse("018f0000-0000-7000-8000-00000000a004");
    private static readonly Guid Item = Guid.Parse("018f0000-0000-7000-8000-00000000a005");
    private static readonly Guid ManualCase = Guid.Parse("018f0000-0000-7000-8000-00000000a006");

    /// <summary>The version ETag the node, checklist and manual-case writes read before their body.</summary>
    private static readonly Action<HttpRequestHeaders> VersionTag =
        headers => headers.TryAddWithoutValidation("If-Match", "\"1\"");

    /// <summary>The sibling-order ETag a reorder reads before its body.</summary>
    private static readonly Action<HttpRequestHeaders> OrderTag =
        headers => headers.TryAddWithoutValidation("If-Match", $"\"order:sha256:{new string('a', 64)}\"");

    /// <summary>The AI settings route's version tag, whose domain starts at the virtual version 0.</summary>
    private static readonly Action<HttpRequestHeaders> SettingsTag =
        headers => headers.TryAddWithoutValidation("If-Match", "\"0\"");

    private static readonly string Authoring = $"/v1/projects/{Project}/authoring";

    /// <summary>Every route, in module order.</summary>
    internal static readonly RawBodyRoute[] Routes =
    [
        new(
            "ingestion.events",
            HttpMethod.Post,
            $"/v1/runs/{Run}/events",
            1_048_576,
            ProducerUnauthenticated,
            """{"events":[]}""",
            DeclaredLimit: 1_048_576,
            HandlerRefusesFirst: true),

        // Orchestrator and its release routes.
        new("orchestrator.manifest", HttpMethod.Post, $"/v1/projects/{Project}/manifests", ManifestCap, Unauthenticated, """{"schema_version":1}"""),
        new("orchestrator.run-create", HttpMethod.Post, $"/v1/projects/{Project}/runs", 64 * 1024, Unauthenticated, """{"manifest_id":"018f0000-0000-7000-8000-00000000a007"}"""),
        new(
            "orchestrator.environment-revision",
            HttpMethod.Post,
            $"/v1/projects/{Project}/environments/staging/revisions",
            64 * 1024,
            Unauthenticated,
            """{"base_urls":{}}"""),
        new("orchestrator.run-cancel", HttpMethod.Post, $"/v1/runs/{Run}/cancel", 4 * 1024, Unauthenticated, """{"reason":"stop tests"}"""),
        new("orchestrator.run-finalize", HttpMethod.Post, $"/v1/runs/{Run}/finalize", 8 * 1024, ProducerUnauthenticated, """{"shard":0}"""),
        new("release.create", HttpMethod.Post, $"/v1/projects/{Project}/releases", 64 * 1024, Unauthenticated, """{"version":"1.0.0"}"""),
        new("release.bind-run", HttpMethod.Post, $"/v1/releases/{Release}/runs", 64 * 1024, Unauthenticated, """{"run_id":"018f0000-0000-7000-8000-00000000a008"}"""),

        // Policy: the append and the activation.
        new(
            "policy.append-revision",
            HttpMethod.Post,
            $"/v1/projects/{Project}/policies",
            64 * 1024,
            Unauthenticated,
            """{"rules":{}}"""),
        new("policy.activate", HttpMethod.Post, $"/v1/projects/{Project}/policy-activation", 4 * 1024, Unauthenticated, """{"policy_revision_id":"018f0000-0000-7000-8000-00000000a009"}"""),

        // Authoring: every write delegate that reads a request body, in the order its endpoint map declares them.
        new("authoring.node-create", HttpMethod.Post, $"{Authoring}/nodes", 64 * 1024, PipelineUnauthenticated, """{"kind":"feature","title":"Example"}"""),
        new("authoring.node-update", HttpMethod.Patch, $"{Authoring}/nodes/{Node}", 64 * 1024, PipelineUnauthenticated, """{"title":"Renamed"}""", VersionTag),
        new("authoring.node-reorder", HttpMethod.Post, $"{Authoring}/nodes/{Node}/reorder", 64 * 1024, PipelineUnauthenticated, """{"node_ids":[]}""", OrderTag),
        new("authoring.node-delete", HttpMethod.Delete, $"{Authoring}/nodes/{Node}", 64 * 1024, PipelineUnauthenticated, """{"reason":"retired"}""", VersionTag),
        new("authoring.checklist-create", HttpMethod.Post, $"{Authoring}/checklist-items", 64 * 1024, PipelineUnauthenticated, """{"item":"Check the response","node_id":"018f0000-0000-7000-8000-00000000a00a"}"""),
        new("authoring.checklist-update", HttpMethod.Patch, $"{Authoring}/checklist-items/{Item}", 64 * 1024, PipelineUnauthenticated, """{"item":"Renamed"}""", VersionTag),
        new("authoring.checklist-delete", HttpMethod.Delete, $"{Authoring}/checklist-items/{Item}", 64 * 1024, PipelineUnauthenticated, """{"reason":"retired"}""", VersionTag),
        new("authoring.manual-case-create", HttpMethod.Post, $"{Authoring}/manual-cases", 64 * 1024, PipelineUnauthenticated, """{"title":"Manual case"}"""),
        new("authoring.manual-case-patch", HttpMethod.Patch, $"{Authoring}/manual-cases/{ManualCase}", 64 * 1024, PipelineUnauthenticated, """{"title":"Renamed"}""", VersionTag),
        new("authoring.manual-case-delete", HttpMethod.Delete, $"{Authoring}/manual-cases/{ManualCase}", 64 * 1024, PipelineUnauthenticated, """{"reason":"retired"}""", VersionTag),
        new("authoring.manual-case-state", HttpMethod.Post, $"{Authoring}/manual-cases/{ManualCase}/state", 64 * 1024, PipelineUnauthenticated, """{"state":"approved"}""", VersionTag),
        new("authoring.manual-case-item", HttpMethod.Post, $"{Authoring}/manual-cases/{ManualCase}/item", 64 * 1024, PipelineUnauthenticated, """{"node_id":"018f0000-0000-7000-8000-00000000a00b"}""", VersionTag),
        new("authoring.manual-case-link", HttpMethod.Post, $"{Authoring}/manual-cases/{ManualCase}/links", 64 * 1024, PipelineUnauthenticated, """{"case_revision_id":"018f0000-0000-7000-8000-00000000a00c"}"""),
        new(
            "authoring.change-queue",
            HttpMethod.Post,
            $"{Authoring}/changes",
            32 * 1024,
            PipelineUnauthenticated,
            """{"kind":"comment","item_id":"018f0000-0000-7000-8000-00000000a00d","text":"Note"}""",
            DeclaredLimit: 32 * 1024,
            HandlerRefusesFirst: true),
        new("authoring.change-apply", HttpMethod.Post, $"{Authoring}/changes:apply", 64 * 1024, PipelineUnauthenticated, """{"change_ids":[]}"""),
        new("authoring.ai-settings", HttpMethod.Put, $"/v1/projects/{Project}/ai-settings", 32 * 1024, PipelineUnauthenticated, """{"settings":{}}""", SettingsTag),
        new(
            "authoring.import",
            HttpMethod.Post,
            $"{Authoring}/import?format=csv",
            5 * 1024 * 1024,
            PipelineUnauthenticated,
            "path,title\nAC1,Example\n",
            DeclaredLimit: 5 * 1024 * 1024,
            HandlerRefusesFirst: true),
    ];
}
