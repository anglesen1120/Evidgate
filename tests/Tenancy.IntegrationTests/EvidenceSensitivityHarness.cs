// 06.2.1 harness, shared by the EvidenceSensitivityTests partials: one tenant per test, rows seeded as the
// migration role, hosts that publish a principal or a service credential, and the audit chain read back.
using System.Net.Http.Headers;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Evidence.Domain;
using Npgsql;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

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

    /// <summary>One seeded artifact: its id and the final object key its row's coordinates minted.</summary>
    private sealed record Seeded(Guid Id, string ObjectKey);

    /// <summary>One audit row, as a denial or a grant writes it.</summary>
    private sealed record AuditEntry(
        string ActorType,
        string ActorId,
        string Action,
        string ObjectType,
        string ObjectId,
        string RequestId,
        string Outcome,
        string RunId,
        string PreviousDefaultSensitivity,
        string Status);

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
            + "coalesce(metadata->>'run_id', ''), "
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
                reader.GetString(8),
                reader.GetString(9)));
        }

        return rows;
    }
}
