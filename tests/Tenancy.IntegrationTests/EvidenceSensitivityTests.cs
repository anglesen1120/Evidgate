// 06.2.1: the artifact read and download routes through the real Api composition, the real C2 pipeline,
// the real evaluator and a migrated PostgreSQL. Every caller is a persisted member of the org, because
// artifact:read is outside ServiceRoleCeiling.
using System.Net;
using System.Net.Http.Headers;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Evidence.Domain;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.2.1 (AC1–AC7) through the real host: the shared gated read model behind the metadata route, the
/// <c>download-url</c> gate with its denial and grant audit rows, cross-tenant and cross-project invisibility,
/// the read-model projections a listing would reuse, and the audited kind-policy override. Rows are seeded as
/// the migration role, requests carry a member principal the evaluator resolves against the real membership
/// tables, and the object store is the CPU-only presigner (SigV4 arithmetic, no container).
/// </summary>
/// <remarks>
/// A member's request is resolved by the production <c>TenantResolver</c>, which answers an org and no project
/// (<c>TenantResolver.ResolveMemberAsync</c>), so the reads here authorize against the artifact's own project; the
/// project-scoped form — a tenant published with a project — is used only to prove that a caller narrowed to another
/// project is refused identically to an unknown id.
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class EvidenceSensitivityTests
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A well-formed content address, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    private const string Checksum = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    /// <summary>The byte count every seeded artifact declares.</summary>
    private const long Bytes = 1024;

    /// <summary>The media types a seeded kind-policy row allows.</summary>
    private static readonly string[] MediaTypes = ["application/zip"];

    /// <summary>
    /// The object-store configuration the download route needs: an unreachable local endpoint (presigning never
    /// connects), path-style addressing and the two credentials under the environment secret provider's spelling.
    /// </summary>
    private static readonly Dictionary<string, string?> ObjectStorage = new(StringComparer.Ordinal)
    {
        ["EVIDGATE:Object:Bucket"] = "evidgate-staging",
        ["EVIDGATE:Object:Region"] = "us-east-1",
        ["EVIDGATE:Object:Endpoint"] = "http://127.0.0.1:9",
        ["EVIDGATE:Object:ForcePathStyle"] = "true",
        ["EVIDGATE:OBJECT:ACCESSKEY"] = "test-access-key",
        ["EVIDGATE:OBJECT:SECRETKEY"] = "test-secret-key",
    };

    /// <summary>No caller headers: the host publishes the principal and tenant itself.</summary>
    private static readonly Action<HttpRequestHeaders> NoCaller = _ => { };

    private readonly PostgresTenancyFixture _fixture;

    public EvidenceSensitivityTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>AC1: a viewer who holds <c>artifact:read</c> and not <c>artifact:read_sensitive</c> is answered a
    /// masked 200 for a sensitive artifact — the key, checksum and byte count are absent, never 403. The caller is
    /// resolved exactly as the production resolver scopes a member: an org and no project.</summary>
    [Fact]
    public async Task GetArtifact_Sensitive_WithoutPermission_ContentGated()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean");
        var viewer = await AddMemberAsync(world, "viewer");
        var member = world.MemberClient(viewer);

        var answer = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}", member.Caller);

        answer.Status.ShouldBe(HttpStatusCode.OK, "only content is gated: the metadata read stays a 200");
        answer.Body.GetProperty("content_gated").GetBoolean().ShouldBeTrue();
        answer.Body.GetProperty("sensitivity").GetString().ShouldBe(ArtifactSensitivity.Sensitive);
        answer.Body.GetProperty("artifact_id").GetGuid().ShouldBe(seeded.Id);
        answer.Body.TryGetProperty("object_key", out _).ShouldBeFalse("a gated body omits the storage key");
        answer.Body.TryGetProperty("checksum", out _).ShouldBeFalse("a gated body omits the content address");
        answer.Body.TryGetProperty("bytes", out _).ShouldBeFalse("a gated body omits the byte count");
    }

    /// <summary>AC1 positive control: the same read with <c>artifact:read_sensitive</c> carries the content fields.</summary>
    [Fact]
    public async Task GetArtifact_Sensitive_WithPermission_ShowsContent()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean");
        var engineer = await AddMemberAsync(world, "qa_engineer");
        var member = world.MemberClient(engineer);

        var answer = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}", member.Caller);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("content_gated").GetBoolean().ShouldBeFalse();
        answer.Body.GetProperty("object_key").GetString().ShouldBe(seeded.ObjectKey);
        answer.Body.GetProperty("checksum").GetString().ShouldBe(Checksum);
        answer.Body.GetProperty("bytes").GetInt64().ShouldBe(Bytes);
    }

    /// <summary>Tenant isolation: another org's artifact id is the same 404 an unknown id answers.</summary>
    [Fact]
    public async Task GetArtifact_CrossTenant_404()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject, otherRun) = await CreateOtherTenantAsync(world);
        var foreign = await SeedArtifactAsync(
            world, ArtifactKind.Trace, ArtifactSensitivity.Normal, "clean",
            org: otherOrg, project: otherProject, runId: otherRun);
        var member = world.MemberClient(world.Founder);

        var answer = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{foreign.Id:D}", member.Caller);
        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("artifact_not_found");

        var unknown = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{Guid.CreateVersion7():D}", member.Caller);
        unknown.Status.ShouldBe(answer.Status, "a foreign id and an unknown id must be one answer");
        unknown.ErrorCode.ShouldBe(answer.ErrorCode);
    }

    /// <summary>The route names no project, so the row's own project decides the scope: a member (org-level roles,
    /// no project in the request) reads a normal artifact of a sibling project, while the same member's request
    /// narrowed to its own project answers the one 404 for it — never the row.</summary>
    [Fact]
    public async Task GetArtifact_OtherProjectOfTheSameOrg_MemberReadsAndNarrowedRequest404()
    {
        await using var world = await StartAsync();
        var otherProject = await AddProjectAsync(world);
        var seeded = await SeedArtifactAsync(
            world, ArtifactKind.RawReport, ArtifactSensitivity.Normal, "clean", project: otherProject);

        var member = world.MemberClient(world.Founder);
        var metadata = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}", member.Caller);
        metadata.Status.ShouldBe(HttpStatusCode.OK, "a member's roles apply to every project of the org");
        metadata.Body.GetProperty("content_gated").GetBoolean().ShouldBeFalse();

        var download = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", member.Caller);
        download.Status.ShouldBe(HttpStatusCode.OK);

        var narrowed = world.ClientFor(RequestPrincipal.Member(world.Founder));
        var narrowedMetadata = await narrowed.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}", NoCaller);
        narrowedMetadata.Status.ShouldBe(HttpStatusCode.NotFound);
        narrowedMetadata.ErrorCode.ShouldBe("artifact_not_found");

        var narrowedDownload = await narrowed.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", NoCaller);
        narrowedDownload.Status.ShouldBe(HttpStatusCode.NotFound);
        narrowedDownload.ErrorCode.ShouldBe("artifact_not_found");
    }

    /// <summary>
    /// The member form of both read routes: a request that carries an org and no project is authorized against the
    /// artifact's own project, so the metadata and the download of a normal artifact are a 200 for that member.
    /// </summary>
    [Fact]
    public async Task GetArtifact_Normal_MemberWithoutProjectScope_200OnMetadataAndDownloadUrl()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.RawReport, ArtifactSensitivity.Normal, "clean");
        var viewer = await AddMemberAsync(world, "viewer");
        var member = world.MemberClient(viewer);

        var metadata = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}", member.Caller);
        metadata.Status.ShouldBe(HttpStatusCode.OK);
        metadata.Body.GetProperty("artifact_id").GetGuid().ShouldBe(seeded.Id);
        metadata.Body.GetProperty("content_gated").GetBoolean().ShouldBeFalse();
        metadata.Body.GetProperty("object_key").GetString().ShouldBe(seeded.ObjectKey);

        var download = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", member.Caller);
        download.Status.ShouldBe(HttpStatusCode.OK);
        download.Body.GetProperty("artifact_id").GetGuid().ShouldBe(seeded.Id);
    }

    /// <summary>
    /// <c>artifact:read</c> is outside the service ceiling, so a project-scoped service credential can never stand in
    /// for a member on either read route: the C2 stage refuses it before any row, exactly as an unknown id would, and
    /// writes no denial audit row. This is the ceiling's promise, not the project scope's — the scope rule only ever
    /// sees a caller that already holds the permission.
    /// </summary>
    [Fact]
    public async Task GetArtifact_ProjectScopedServiceCredential_403BeforeAnyRow()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.RawReport, ArtifactSensitivity.Normal, "clean");
        var service = world.ServiceClient(world.Project, "artifact:read");

        var metadata = await service.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}", service.Caller);
        metadata.Status.ShouldBe(HttpStatusCode.Forbidden);
        metadata.ErrorCode.ShouldBe("forbidden");

        var unknown = await service.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{Guid.CreateVersion7():D}", service.Caller);
        unknown.Status.ShouldBe(metadata.Status);
        unknown.ErrorCode.ShouldBe(metadata.ErrorCode);

        var download = await service.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", service.Caller);
        download.Status.ShouldBe(HttpStatusCode.Forbidden);
        download.ErrorCode.ShouldBe("forbidden");

        (await AuditRowsAsync(world, "artifact.download_denied")).ShouldBeEmpty(
            "a caller without artifact:read must not be able to probe for rows");
    }

    /// <summary>AC2: a download of a sensitive artifact without the permission is 403 <c>sensitivity_forbidden</c>,
    /// and the denial's audit row is committed before the refusal renders.</summary>
    [Fact]
    public async Task DownloadUrl_Sensitive_WithoutPermission_403AndAudited()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean");
        var viewer = await AddMemberAsync(world, "viewer");

        var member = world.MemberClient(viewer);

        var answer = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", member.Caller);

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("sensitivity_forbidden");

        var audits = await AuditRowsAsync(world, "artifact.download_denied", seeded.Id.ToString("D"));
        var row = audits.ShouldHaveSingleItem();
        row.ActorType.ShouldBe("user");
        row.ActorId.ShouldBe(viewer.ToString("D"));
        row.ObjectType.ShouldBe("artifact");
        row.ObjectId.ShouldBe(seeded.Id.ToString("D"));
        string.IsNullOrWhiteSpace(row.RequestId).ShouldBeFalse("the denial carries the request id");
        row.Outcome.ShouldBe("sensitive_forbidden");
    }

    /// <summary>AC3: the same route with <c>artifact:read_sensitive</c> issues a short-lived presigned URL and
    /// records the sensitive grant.</summary>
    [Fact]
    public async Task DownloadUrl_Sensitive_WithPermission_200()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean");
        var engineer = await AddMemberAsync(world, "qa_engineer");

        var member = world.MemberClient(engineer);

        var answer = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", member.Caller);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("artifact_id").GetGuid().ShouldBe(seeded.Id);

        var url = answer.Body.GetProperty("download_url").GetString() ?? string.Empty;
        url.ShouldStartWith("http://127.0.0.1:9/evidgate-staging/");
        url.ShouldContain(seeded.ObjectKey, Case.Sensitive);
        url.ShouldContain("X-Amz-Signature=", Case.Sensitive);
        answer.Body.GetProperty("expires_at").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        var audits = await AuditRowsAsync(world, "artifact.download", seeded.Id.ToString("D"));
        var row = audits.ShouldHaveSingleItem();
        row.ActorId.ShouldBe(engineer.ToString("D"));
        row.Outcome.ShouldBe("sensitive_granted");
    }

    /// <summary>
    /// A caller without <c>artifact:read</c> is refused before any row is read: the existing and the unknown id
    /// answer the identical 403, so the route is no oracle, and neither denial writes an audit row.
    /// </summary>
    [Fact]
    public async Task DownloadUrl_WithoutArtifactRead_SameRefusalForExistingAndUnknown()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean");
        var unrelated = await AddMemberAsync(world, roleCode: null);
        var member = world.MemberClient(unrelated);

        var existing = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", member.Caller);
        var unknown = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{Guid.CreateVersion7():D}/download-url", member.Caller);

        existing.Status.ShouldBe(HttpStatusCode.Forbidden);
        existing.ErrorCode.ShouldBe("forbidden");
        unknown.Status.ShouldBe(existing.Status);
        unknown.ErrorCode.ShouldBe(existing.ErrorCode);
        unknown.ErrorMessage.ShouldBe(existing.ErrorMessage);
        (await AuditRowsAsync(world, "artifact.download_denied")).ShouldBeEmpty(
            "a principal without artifact:read must not be able to probe for rows");
    }

    /// <summary>Only a <c>ready</c> row has bytes at its final key: a non-ready row is a 409, not a URL.</summary>
    [Fact]
    public async Task DownloadUrl_NonReadyArtifact_Is409()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(
            world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean", state: ArtifactState.Uploaded);

        var member = world.MemberClient(world.Founder);

        var answer = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", member.Caller);

        answer.Status.ShouldBe(HttpStatusCode.Conflict);
        answer.ErrorCode.ShouldBe("artifact_state_conflict");
        answer.Body.TryGetProperty("download_url", out _).ShouldBeFalse();
        (await AuditRowsAsync(world, "artifact.download", seeded.Id.ToString("D"))).ShouldBeEmpty(
            "nothing was issued, so nothing is recorded as granted");
    }

    /// <summary>No gated or denied answer carries the storage key, the content address or a presigned URL.</summary>
    [Fact]
    public async Task GatedAndDenied_Bodies_CarryNoStorageValues()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean");
        var viewer = await AddMemberAsync(world, "viewer");
        var member = world.MemberClient(viewer);

        var metadata = await member.Client.SendAsync(HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}", member.Caller);
        metadata.Status.ShouldBe(HttpStatusCode.OK);
        var metadataBody = metadata.Body.GetRawText();
        metadataBody.ShouldNotContain("object_key", Case.Sensitive);
        metadataBody.ShouldNotContain("checksum", Case.Sensitive);
        metadataBody.ShouldNotContain("download_url", Case.Sensitive);
        metadataBody.ShouldNotContain(seeded.ObjectKey, Case.Sensitive);
        metadataBody.ShouldNotContain(Checksum, Case.Sensitive);

        var denied = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", member.Caller);
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        var deniedBody = denied.Body.GetRawText();
        deniedBody.ShouldNotContain("download_url", Case.Sensitive);
        deniedBody.ShouldNotContain("X-Amz-Signature", Case.Sensitive);
        deniedBody.ShouldNotContain(seeded.ObjectKey, Case.Sensitive);
        deniedBody.ShouldNotContain(Checksum, Case.Sensitive);
    }

    // Harness: one tenant per test, rows seeded as the migration role, and the audit chain read back.

    /// <summary>One seeded artifact: its id and the final object key its row's coordinates minted.</summary>
    private sealed record Seeded(Guid Id, string ObjectKey);

    /// <summary>One audit row, as a denial or a grant writes it.</summary>
    private sealed record AuditEntry(
        string ActorType, string ActorId, string Action, string ObjectType, string ObjectId,
        string RequestId, string Outcome, string PreviousDefaultSensitivity, string Status);

    /// <summary>One test's org, project and run, plus every host and client it opened (disposed together).</summary>
    private sealed class World(
        TenancyApiFactory api,
        string connectionString,
        string database,
        Guid founder,
        Guid org,
        Guid project,
        Guid runId) : IAsyncDisposable
    {
        private readonly List<TenancyApiFactory> _hosts = [];
        private readonly List<HttpClient> _clients = [];

        /// <summary>The setup host (the real resolver and evaluator) that created the tenant.</summary>
        internal TenancyApiFactory Api { get; } = api;

        internal string Database { get; } = database;

        internal Guid Founder { get; } = founder;

        internal Guid Org { get; } = org;

        internal Guid Project { get; } = project;

        internal Guid RunId { get; } = runId;

        /// <summary>A host whose every request runs as <paramref name="principal"/> inside this tenant.</summary>
        internal TenancyApiFactory HostFor(RequestPrincipal principal)
        {
            var host = TenancyApiFactory.WithPrincipal(
                connectionString, Org, Project, principal, ObjectStorage);
            _hosts.Add(host);
            return host;
        }

        /// <summary>A host whose requests run as <paramref name="principal"/> with the tenant narrowed to project
        /// <paramref name="project"/> — the project-scoped form a service or published request carries.</summary>
        internal TenancyApiFactory HostFor(RequestPrincipal principal, Guid project)
        {
            var host = TenancyApiFactory.WithPrincipal(
                connectionString, Org, project, principal, ObjectStorage);
            _hosts.Add(host);
            return host;
        }

        /// <summary>A client of a host that publishes <paramref name="principal"/> for every request.</summary>
        internal HttpClient ClientFor(RequestPrincipal principal)
        {
            var client = HostFor(principal).CreateClient();
            _clients.Add(client);
            return client;
        }

        /// <summary>A client of a host whose requests are narrowed to project <paramref name="project"/>.</summary>
        internal HttpClient ClientFor(RequestPrincipal principal, Guid project)
        {
            var client = HostFor(principal, project).CreateClient();
            _clients.Add(client);
            return client;
        }

        /// <summary>
        /// The setup host's client (the real resolver and evaluator) plus the headers of the production member
        /// resolution: the user and the org header, so <c>TenantResolver.ResolveMemberAsync</c> answers an org and no
        /// project — exactly what a signed-in member's request carries.
        /// </summary>
        /// <param name="user">The member's user id.</param>
        internal (HttpClient Client, Action<HttpRequestHeaders> Caller) MemberClient(Guid user)
        {
            var client = Api.CreateClient();
            _clients.Add(client);
            return (client, TenancyApiFactory.Resolved(user: user, orgHeader: Org));
        }

        /// <summary>
        /// The setup host's client plus a project-scoped service credential: the tenant is narrowed to
        /// <paramref name="project"/> and the claim is the permission set the caller asks for, filtered by the
        /// server to the service ceiling — so a code outside it is resolved to no permission at all.
        /// </summary>
        /// <param name="project">The project the credential is bound to.</param>
        /// <param name="permissions">The codes the credential claims.</param>
        internal (HttpClient Client, Action<HttpRequestHeaders> Caller) ServiceClient(
            Guid project,
            params string[] permissions)
        {
            var client = Api.CreateClient();
            _clients.Add(client);
            return (client, TenancyApiFactory.Resolved(
                serviceOrg: Org, serviceProject: project, servicePermissions: permissions));
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }

            foreach (var host in _hosts)
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }

            await Api.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The shared database with this test's own org and project, plus one run row for its artifacts.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var connectionString = _fixture.AppConnectionString(database);
        var api = TenancyApiFactory.RealResolver(connectionString, ObjectStorage);
        Guid founder;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            founder = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, founder, Unique.Slug("sensitivity"));
            project = await CreateProjectAsync(setup, founder, org, Unique.Slug("sensitivity-project"));
        }

        var runId = await RunRowSeeder.SeedRunAsync(_fixture, database, org);
        return new World(api, connectionString, database, founder, org, project, runId);
    }

    /// <summary>A second org with a project and a run, created through the real routes.</summary>
    private async Task<(Guid Org, Guid Project, Guid RunId)> CreateOtherTenantAsync(World world)
    {
        using var setup = world.Api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(setup, user, Unique.Slug("other"));
        var project = await CreateProjectAsync(setup, user, org, Unique.Slug("other-project"));
        var run = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, org);
        return (org, project, run);
    }

    /// <summary>A second project in the test's own org, created as the founder.</summary>
    private static async Task<Guid> AddProjectAsync(World world)
    {
        using var setup = world.Api.CreateClient();
        return await CreateProjectAsync(setup, world.Founder, world.Org, Unique.Slug("second-project"));
    }

    /// <summary>An active member holding one built-in role, or none when <paramref name="roleCode"/> is null.</summary>
    private async Task<Guid> AddMemberAsync(World world, string? roleCode)
    {
        var user = Guid.CreateVersion7();
        var email = $"member-{user:N}@example.test";

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public."user" (id, email) values ('{user:D}', '{email}');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values (gen_random_uuid(), '{world.Org:D}', '{user:D}', '{email}', 'active');
            """);

        if (roleCode is not null)
        {
            await _fixture.ExecuteAsync(
                world.Database,
                $"""
                insert into public.membership_role (id, org_id, membership_id, role_id)
                select gen_random_uuid(), '{world.Org:D}', m.id, r.id
                  from public.membership m, public.role r
                 where m.org_id = '{world.Org:D}' and m.user_id = '{user:D}'
                   and r.code = '{roleCode}' and r.org_id is null;
                """);
        }

        return user;
    }

    /// <summary>One artifact row, seeded as the migration role with the final object key its coordinates mint.</summary>
    private async Task<Seeded> SeedArtifactAsync(
        World world,
        string kind,
        string sensitivity,
        string scanState,
        string state = ArtifactState.Ready,
        int attemptNo = 1,
        Guid? org = null,
        Guid? project = null,
        Guid? runId = null)
    {
        var owner = org ?? world.Org;
        var ownerProject = project ?? world.Project;
        var run = runId ?? world.RunId;
        var id = Guid.CreateVersion7();
        var objectKey =
            $"org/{owner:D}/project/{ownerProject:D}/run/{run:D}/attempt/{ExecutionKey}/{attemptNo}/{id:D}";

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, "
            + "media_type, checksum, sensitivity, scan_state, state, bytes) "
            + $"values ('{id:D}', '{owner:D}', '{ownerProject:D}', '{run:D}', '{ExecutionKey}', {attemptNo}, "
            + $"'{kind}', '{objectKey}', 'application/zip', '{Checksum}', '{sensitivity}', '{scanState}', '{state}', {Bytes})");

        return new Seeded(id, objectKey);
    }

    /// <summary>One committed kind-policy row for a project.</summary>
    private Task SeedPolicyAsync(
        World world,
        string kind,
        string sensitivity,
        Guid? project = null,
        long maxBytes = 4096,
        bool captureEnabled = true,
        bool rejectOnSecret = true) =>
        _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, "
            + "default_sensitivity, reject_on_secret, capture_enabled) "
            + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{project ?? world.Project:D}', '{kind}', "
            + $"{maxBytes}, array['application/zip'], '{sensitivity}', {rejectOnSecret.ToString().ToLowerInvariant()}, "
            + $"{captureEnabled.ToString().ToLowerInvariant()})");

    /// <summary>The committed policy rows of one project, as <c>kind|max_bytes|sensitivity|capture|reject</c>.</summary>
    private Task<IReadOnlyList<string>> PolicyRowsAsync(World world, Guid project, Guid? org = null) =>
        _fixture.ColumnAsync<string>(
            world.Database,
            "select kind || '|' || max_bytes || '|' || default_sensitivity || '|' || capture_enabled || '|' "
            + "|| reject_on_secret from public.artifact_kind_policy "
            + $"where org_id = '{org ?? world.Org:D}' and project_id = '{project:D}' order by kind");

    /// <summary>Audit rows of one action (and object, when named), oldest first.</summary>
    private async Task<IReadOnlyList<AuditEntry>> AuditRowsAsync(World world, string action, string? objectId = null)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(world.Database);
        await using var command = new NpgsqlCommand(
            "select actor_type, actor_id, action, coalesce(object_type, ''), coalesce(object_id, ''), "
            + "coalesce(request_id, ''), coalesce(metadata->>'outcome', ''), "
            + "coalesce(metadata->>'previous_default_sensitivity', ''), coalesce(metadata->>'status', '') "
            + "from public.audit_log "
            + "where org_id = @org and action = @action"
            + (objectId is null ? string.Empty : " and object_id = @object_id")
            + " order by seq",
            connection);
        command.Parameters.AddWithValue("org", world.Org);
        command.Parameters.AddWithValue("action", action);
        if (objectId is not null)
        {
            command.Parameters.AddWithValue("object_id", objectId);
        }

        var rows = new List<AuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add(new AuditEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return rows;
    }
}
