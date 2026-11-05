// Capture options of one project: the read route, the redaction-rule allowlist on the existing kind-policy write, the
// cross-module reader and the upload gate the switch already owned. Every assertion reads the committed rows back as the
// migration role, so "nothing was written" is proven rather than inferred.
using System.Net;
using System.Net.Http.Headers;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Evidence.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The project's capture control through the real host and a real PostgreSQL: <c>GET
/// /v1/projects/{p}/artifact-kind-policy</c> answers every kind's effective switch and declared rules, the existing
/// override route writes them under <c>project:write</c>, a rule type outside the four contract types is refused 422
/// before any row, and <see cref="ICaptureOptionsReader"/> answers the same facts to another module without crossing a
/// tenant boundary. A switch set to false keeps refusing the kind at <c>upload-url</c>, which is the gate that already
/// existed and is asserted end-to-end here.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class EvidenceCaptureOptionsTests
{
    /// <summary>A well-formed execution key: sha256 hex.</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A well-formed declared content address.</summary>
    private const string Checksum = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string ProducerId = "shard-1";

    private const string Jti = "jti-capture-options-1";

    /// <summary>The object-store configuration the upload route needs: presigning never connects.</summary>
    private static readonly Dictionary<string, string?> ObjectStorage = new(StringComparer.Ordinal)
    {
        ["EVIDGATE:Object:Bucket"] = "evidgate-staging",
        ["EVIDGATE:Object:Region"] = "us-east-1",
        ["EVIDGATE:Object:Endpoint"] = "http://127.0.0.1:9",
        ["EVIDGATE:Object:ForcePathStyle"] = "true",
        ["EVIDGATE:OBJECT:ACCESSKEY"] = "test-access-key",
        ["EVIDGATE:OBJECT:SECRETKEY"] = "test-secret-key",
    };

    private static readonly Action<HttpRequestHeaders> NoCaller = _ => { };

    private readonly PostgresTenancyFixture _fixture;

    public EvidenceCaptureOptionsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>One test's tenant, its hosts and the database every assertion reads.</summary>
    private sealed class World(
        TenancyApiFactory api,
        string connectionString,
        string database,
        Guid founder,
        Guid org,
        Guid project) : IAsyncDisposable
    {
        private readonly List<TenancyApiFactory> _hosts = [];
        private readonly List<HttpClient> _clients = [];

        internal TenancyApiFactory Api { get; } = api;

        internal string ConnectionString { get; } = connectionString;

        internal string Database { get; } = database;

        internal Guid Founder { get; } = founder;

        internal Guid Org { get; } = org;

        internal Guid Project { get; } = project;

        /// <summary>A client of a host that publishes <paramref name="principal"/> for every request.</summary>
        internal HttpClient ClientFor(RequestPrincipal principal)
        {
            var host = TenancyApiFactory.WithPrincipal(
                ConnectionString, Org, Project, principal, ObjectStorage);
            _hosts.Add(host);
            var client = host.CreateClient();
            _clients.Add(client);
            return client;
        }

        /// <summary>The setup host's client plus the headers of the production member resolution.</summary>
        internal (HttpClient Client, Action<HttpRequestHeaders> Caller) MemberClient(Guid user)
        {
            var client = Api.CreateClient();
            _clients.Add(client);
            return (client, TenancyApiFactory.Resolved(user: user, orgHeader: Org));
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

    /// <summary>One database with an org and a project created through the real registry routes.</summary>
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
            org = await CreateOrgAsync(setup, founder, Unique.Slug("capture"));
            project = await CreateProjectAsync(setup, founder, org, Unique.Slug("capture-project"));
        }

        return new World(api, connectionString, database, founder, org, project);
    }

    /// <summary>The read route's URL for one project.</summary>
    private static string OptionsUrl(Guid project) => $"/v1/projects/{project:D}/artifact-kind-policy";

    /// <summary>The override route's URL for one project and kind.</summary>
    private static string PolicyUrl(Guid project, string kind) =>
        $"/v1/projects/{project:D}/artifact-kind-policy/{kind}";

    /// <summary>
    /// A complete policy body: the five knobs, plus the rules member when the case sends one. <c>null</c> omits the
    /// member entirely — the "leave the declared set alone" case — while <c>"null"</c> sends it as a JSON null.
    /// </summary>
    private static string PolicyBody(
        string sensitivity = "normal",
        bool captureEnabled = true,
        string? rules = "null") =>
        rules is null
            ? $$"""
            {"max_bytes":2048,"media_types":["application/zip"],"default_sensitivity":"{{sensitivity}}","capture_enabled":{{(captureEnabled ? "true" : "false")}},"reject_on_secret":false}
            """
            : $$"""
            {"max_bytes":2048,"media_types":["application/zip"],"default_sensitivity":"{{sensitivity}}","capture_enabled":{{(captureEnabled ? "true" : "false")}},"reject_on_secret":false,"redaction_rules":{{rules}}}
            """;

    /// <summary>An active member holding one built-in role.</summary>
    private async Task<Guid> AddMemberAsync(World world, string roleCode)
    {
        var user = Guid.CreateVersion7();
        var email = $"member-{user:N}@example.test";

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public."user" (id, email) values ('{user:D}', '{email}');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values (gen_random_uuid(), '{world.Org:D}', '{user:D}', '{email}', 'active');
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), '{world.Org:D}', m.id, r.id
              from public.membership m, public.role r
             where m.org_id = '{world.Org:D}' and m.user_id = '{user:D}'
               and r.code = '{roleCode}' and r.org_id is null;
            """);

        return user;
    }

    /// <summary>The stored rules of one project, as <c>kind|rule_type|name_pattern</c>.</summary>
    private Task<IReadOnlyList<string>> RuleRowsAsync(World world, Guid? project = null, Guid? org = null) =>
        _fixture.ColumnAsync<string>(
            world.Database,
            "select kind || '|' || rule_type || '|' || name_pattern from public.redaction_rule "
            + $"where org_id = '{org ?? world.Org:D}' and project_id = '{project ?? world.Project:D}' "
            + "order by kind, rule_type, name_pattern");

    /// <summary>The production member view of the read route's answer: kind → (capture switch, rules).</summary>
    private static Dictionary<string, (bool Enabled, IReadOnlyList<string> Rules)> Options(ApiAnswer answer)
    {
        var options = new Dictionary<string, (bool, IReadOnlyList<string>)>(StringComparer.Ordinal);
        foreach (var kind in answer.Body.GetProperty("kinds").EnumerateArray())
        {
            var rules = kind.GetProperty("redaction_rules").EnumerateArray()
                .Select(rule => rule.GetProperty("rule_type").GetString() + "|" + rule.GetProperty("name_pattern").GetString())
                .ToList();
            options[kind.GetProperty("kind").GetString()!] =
                (kind.GetProperty("capture_enabled").GetBoolean(), rules);
        }

        return options;
    }

    [Fact]
    public async Task CaptureOptions_Defaults_EveryKindOnceWithCaptureOnAndNoRules()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        var answer = await member.Client.SendAsync(
            HttpMethod.Get, OptionsUrl(world.Project), member.Caller);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        // The raw answer, not only the parsed map: one entry per contract kind, in contract order, every kind once.
        answer.Body.GetProperty("kinds").GetArrayLength().ShouldBe(8);
        answer.Body.GetProperty("kinds").EnumerateArray()
            .Select(kind => kind.GetProperty("kind").GetString())
            .ShouldBe(ArtifactKinds.All);
        var options = Options(answer);
        options.Keys.ShouldBe(ArtifactKinds.All);
        options["trace"].Enabled.ShouldBeTrue();
        options["raw_report"].Rules.ShouldBeEmpty();
        // The four kinds that fix no static cap stay fail-closed with no row, and the read says so instead of claiming
        // a capture the upload route would refuse.
        options["dom_snapshot"].Enabled.ShouldBeFalse();
        options["ui_hierarchy"].Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task CaptureOptions_DeclaredRules_AreServedAndReplaced()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        var written = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller,
            PolicyBody(rules: """[{"rule_type":"header","name_pattern":"authorization"},{"rule_type":"cookie","name_pattern":"session*"}]"""));

        written.Status.ShouldBe(HttpStatusCode.OK, written.ErrorMessage);
        written.Body.GetProperty("redaction_rules").GetArrayLength().ShouldBe(2);
        (await RuleRowsAsync(world)).ShouldBe(["trace|cookie|session*", "trace|header|authorization"]);

        var served = Options(await member.Client.SendAsync(
            HttpMethod.Get, OptionsUrl(world.Project), member.Caller));
        // The read orders by rule_type, so cookie sorts before header whatever order the write declared them in.
        served["trace"].Rules.ShouldBe(["cookie|session*", "header|authorization"]);

        // An omitted member leaves the declared rules alone: a caller that only toggles a switch cannot delete a
        // privacy control it never sent.
        var kept = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller, PolicyBody(captureEnabled: false, rules: null));
        kept.Status.ShouldBe(HttpStatusCode.OK, kept.ErrorMessage);
        kept.Body.GetProperty("redaction_rules").GetArrayLength().ShouldBe(2);
        (await RuleRowsAsync(world)).ShouldBe(["trace|cookie|session*", "trace|header|authorization"]);

        // The member sent as a JSON null is the same as omitting it, so a caller that serialises a missing list as
        // null still cannot clear a privacy control by accident.
        var nulled = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller, PolicyBody(rules: "null"));
        nulled.Status.ShouldBe(HttpStatusCode.OK, nulled.ErrorMessage);
        nulled.Body.GetProperty("redaction_rules").GetArrayLength().ShouldBe(2);
        (await RuleRowsAsync(world)).ShouldBe(["trace|cookie|session*", "trace|header|authorization"]);

        // An empty list clears them, and the answer says so.
        var cleared = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller, PolicyBody(rules: "[]"));
        cleared.Status.ShouldBe(HttpStatusCode.OK, cleared.ErrorMessage);
        cleared.Body.GetProperty("redaction_rules").GetArrayLength().ShouldBe(0);
        (await RuleRowsAsync(world)).ShouldBeEmpty();
    }

    [Fact]
    public async Task CaptureOptions_UnknownRuleType_422AndNoRows()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        var answer = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller,
            PolicyBody(rules: """[{"rule_type":"path_segment","name_pattern":"/users/*"}]"""));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("redaction_rule_type_unknown");
        answer.Body.GetRawText().ShouldNotContain("path_segment");
        (await RuleRowsAsync(world)).ShouldBeEmpty();
        (await PolicyRowsAsync(world)).ShouldBeEmpty("the refusal lands before the policy row is written");
    }

    [Fact]
    public async Task CaptureOptions_MalformedRule_422AndNoRows()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        var badPattern = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller,
            PolicyBody(rules: """[{"rule_type":"header","name_pattern":"a b"}]"""));
        badPattern.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        badPattern.ErrorCode.ShouldBe("request_invalid");

        var repeated = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller,
            PolicyBody(rules: """[{"rule_type":"header","name_pattern":"x"},{"rule_type":"header","name_pattern":"x"}]"""));
        repeated.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        repeated.ErrorCode.ShouldBe("request_invalid");

        (await RuleRowsAsync(world)).ShouldBeEmpty();
        (await PolicyRowsAsync(world)).ShouldBeEmpty();
    }

    [Fact]
    public async Task CaptureOptions_NonAdmin_403AndNothingWritten()
    {
        await using var world = await StartAsync();
        var engineer = await AddMemberAsync(world, "qa_engineer");
        var member = world.MemberClient(engineer);

        var write = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller,
            PolicyBody(rules: """[{"rule_type":"header","name_pattern":"authorization"}]"""));
        write.Status.ShouldBe(HttpStatusCode.Forbidden);

        var read = await member.Client.SendAsync(HttpMethod.Get, OptionsUrl(world.Project), member.Caller);
        read.Status.ShouldBe(HttpStatusCode.OK, "the read is project:read, so a read-only member may serve it");

        (await RuleRowsAsync(world)).ShouldBeEmpty();
        (await PolicyRowsAsync(world)).ShouldBeEmpty();
    }

    [Fact]
    public async Task CaptureOptions_OtherTenantProject_404AndRulesStayHome()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        // Org B declares a rule; org A must never read it, and must not learn that the project exists.
        Guid otherOrg;
        Guid otherProject;
        using (var setup = world.Api.CreateClient())
        {
            var otherUser = Guid.CreateVersion7();
            otherOrg = await CreateOrgAsync(setup, otherUser, Unique.Slug("other-capture"));
            otherProject = await CreateProjectAsync(setup, otherUser, otherOrg, Unique.Slug("other-capture-project"));
        }

        var otherMember = world.Api.CreateClient();
        using (otherMember)
        {
            var written = await otherMember.SendRawAsync(
                HttpMethod.Put, PolicyUrl(otherProject, "trace"),
                TenancyApiFactory.Resolved(user: world.Founder, orgHeader: world.Org),
                PolicyBody(rules: """[{"rule_type":"query_param","name_pattern":"token"}]"""));
            written.Status.ShouldBe(HttpStatusCode.NotFound, "org A cannot write org B's project either");
        }

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.redaction_rule (id, org_id, project_id, kind, rule_type, name_pattern) "
            + $"values ('{Guid.CreateVersion7():D}', '{otherOrg:D}', '{otherProject:D}', 'trace', 'query_param', 'token')");

        var foreign = await member.Client.SendAsync(HttpMethod.Get, OptionsUrl(otherProject), member.Caller);
        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
        foreign.ErrorCode.ShouldBe("not_found");
        foreign.Body.GetRawText().ShouldNotContain("token");

        var unknown = await member.Client.SendAsync(HttpMethod.Get, OptionsUrl(Guid.CreateVersion7()), member.Caller);
        unknown.Status.ShouldBe(foreign.Status);
    }

    [Fact]
    public async Task CaptureOptions_Reader_ServesDisabledKindAndRulesWithoutCrossingTenants()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        // A stored switch that is off, distinct from the kinds that are off only because they have no default: a
        // reader that ignored the persisted row would answer true here and fail.
        (await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "video"), member.Caller,
            PolicyBody(
                captureEnabled: false,
                rules: """[{"rule_type":"form_field","name_pattern":"password*"}]""")))
            .Status.ShouldBe(HttpStatusCode.OK);

        var services = world.Api.Services.CreateAsyncScope();
        await using (services)
        {
            var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
            await using var scope = await scopes.BeginAsync(world.Org, world.Project, false, Token);
            var reader = services.ServiceProvider.GetRequiredService<ICaptureOptionsReader>();

            var options = await reader.GetAsync(world.Org, world.Project, Token);
            options.Select(option => option.Kind).ShouldBe(ArtifactKinds.All);
            options.Single(option => option.Kind == "video").CaptureEnabled.ShouldBeFalse();
            options.Single(option => option.Kind == "video").RedactionRules
                .ShouldBe([new RedactionRule("form_field", "password*")]);
            options.Single(option => option.Kind == "dom_snapshot").CaptureEnabled.ShouldBeFalse();

            // Another org's id yields no row, so the reader answers the defaults and never a rule.
            var foreign = await reader.GetAsync(Guid.CreateVersion7(), world.Project, Token);
            foreign.SelectMany(option => option.RedactionRules).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task CaptureOptions_DisabledKind_StillRefusesUploadUrl422()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);

        (await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "trace"), member.Caller, PolicyBody(captureEnabled: false)))
            .Status.ShouldBe(HttpStatusCode.OK);

        var runId = await SeedRunAsync(world);
        var writer = world.ClientFor(RequestPrincipal.Service(
            projectScope: null, [Permissions.Artifact.Write], credentialId: Jti));

        var answer = await writer.SendAsync(
            HttpMethod.Post, "/v1/artifacts/upload-url", NoCaller,
            new
            {
                run_id = runId,
                producer_id = ProducerId,
                execution_key = ExecutionKey,
                attempt_no = 1,
                kind = "trace",
                media_type = "application/zip",
                bytes = 1024,
                sha256 = Checksum,
            },
            NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("kind_disabled");
    }

    /// <summary>The committed policy rows of one project, as <c>kind|max_bytes|sensitivity|capture|reject</c>.</summary>
    private Task<IReadOnlyList<string>> PolicyRowsAsync(World world, Guid? project = null, Guid? org = null) =>
        _fixture.ColumnAsync<string>(
            world.Database,
            "select kind || '|' || max_bytes || '|' || default_sensitivity || '|' || capture_enabled || '|' "
            + "|| reject_on_secret from public.artifact_kind_policy "
            + $"where org_id = '{org ?? world.Org:D}' and project_id = '{project ?? world.Project:D}' order by kind");

    /// <summary>One committed run with a planned execution and a producer slot for the upload route's parents.</summary>
    private async Task<Guid> SeedRunAsync(World world)
    {
        var runId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

        await using var scope = await TenantScope.BeginAsync(world.ConnectionString, world.Org);
        var testRevision = await scope.ScalarAsync<Guid>(
            "select id from public.test_revision where org_id = @org_id limit 1", ("org_id", world.Org));

        (await scope.ExecuteAsync(
            "insert into public.planned_execution (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id, required) "
            + "values (@id, @org_id, @run_id, @execution_key, @case_revision_id, @variant_hash, 'staging', 'shard-1', true)",
            ("id", Guid.CreateVersion7()), ("org_id", world.Org), ("run_id", runId), ("execution_key", ExecutionKey),
            ("case_revision_id", testRevision), ("variant_hash", "sha256:" + new string('c', 64)))).ShouldBe(1);

        (await scope.ExecuteAsync(
            "insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id, bound_token_jti) "
            + "values (@id, @org_id, @run_id, 1, @producer_id, @jti)",
            ("id", Guid.CreateVersion7()), ("org_id", world.Org), ("run_id", runId), ("producer_id", ProducerId),
            ("jti", Jti))).ShouldBe(1);

        await scope.CommitAsync();

        return runId;
    }
}
