using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.1.1-T4 AC4 through the real host and a real PostgreSQL: <c>POST /v1/projects/{p}/policy-activation</c>
/// moves the project's policy pointer, retires the previous active revision, keeps exactly one active row,
/// writes exactly one audit row per call (refusals write nothing), and refuses the retired-revision, the
/// critical-case and the permission cases with the documented codes.
/// </summary>
/// <remarks>
/// The routes, status codes, wire names and error codes are the 07.1.1-T4 contract (07.1.1 §5/§6); the tests
/// drive the HTTP surface only, so they do not depend on the module's internal type names. The run-pinning half
/// of AC4 (a run created before a second activation keeps its pin) lives in <c>PolicyRunPinningTests</c>.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class PolicyActivationTests
{
    private const string BodyA = """{"flaky_budget":3,"required_suites":["smoke","regression"]}""";

    private const string BodyB = """{"flaky_budget":4,"required_suites":["smoke","regression"]}""";

    private const string BodyC = """{"flaky_budget":5,"required_suites":["smoke","regression"]}""";

    private readonly PostgresTenancyFixture _fixture;

    public PolicyActivationTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        return new World(api, client, database, user, org, project);
    }

    /// <summary>Appends a revision through the documented route and returns its id.</summary>
    private static async Task<Guid> CreateRevisionAsync(World world, string body, Guid? project = null)
    {
        var created = await SendRawAsync(
            world.Client,
            HttpMethod.Post,
            $"/v1/projects/{project ?? world.Project}/policies",
            world.Admin,
            body);
        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id();
    }

    /// <summary>
    /// Activates <paramref name="revisionId"/> in <paramref name="project"/> as the admin. Every call carries an
    /// <c>Idempotency-Key</c> (07.1.1-T4, 00 §9.1): a fresh one by default, or <paramref name="idempotencyKey"/> to
    /// replay/conflict, or none at all when <paramref name="omitIdempotencyKey"/> is set (the missing-header case).
    /// </summary>
    private static Task<ApiAnswer> ActivateAsync(
        World world,
        Guid revisionId,
        Guid? project = null,
        Action<HttpRequestHeaders>? caller = null,
        string? rawBody = null,
        string? idempotencyKey = null,
        bool omitIdempotencyKey = false) =>
        SendRawAsync(
            world.Client,
            HttpMethod.Post,
            $"/v1/projects/{project ?? world.Project}/policy-activation",
            caller ?? world.Admin,
            rawBody ?? $$"""{"policy_revision_id":"{{revisionId}}"}""",
            omitIdempotencyKey ? null : idempotencyKey ?? NewKey());

    /// <summary>
    /// Sends a raw body verbatim. An <c>Idempotency-Key</c> header is added only when <paramref name="idempotencyKey"/>
    /// is non-null: the activation route requires one, the revision route takes none.
    /// </summary>
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

    private static string NewKey() => Guid.NewGuid().ToString("N");

    private async Task<string> StateAsync(World world, Guid revisionId) =>
        await _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.policy_revision where id = '{revisionId}'");

    private async Task<long> ActiveCountAsync(World world) =>
        await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.policy_revision where state = 'active' and org_id = '{world.Org:D}'");

    private async Task<long> ActivationAuditRowsAsync(World world) =>
        await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where action = 'policy.activate' and org_id = '{world.Org:D}'");

    private async Task<Guid> PointerAsync(World world) =>
        await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select policy_revision_id from public.policy_activation where project_id = '{world.Project}'");

    /// <summary>Adds an active member of <paramref name="roleCode"/> to the org and returns her user id.</summary>
    private async Task<Guid> AddMemberAsync(World world, string roleCode)
    {
        var user = Guid.CreateVersion7();
        var email = $"{roleCode}-{user:N}@example.test";
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public."user" (id, email) values ('{user}', '{email}');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values (gen_random_uuid(), '{world.Org}', '{user}', '{email}', 'active');
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), '{world.Org}', m.id, r.id
              from public.membership m, public.role r
             where m.org_id = '{world.Org}' and m.user_id = '{user}'
               and r.code = '{roleCode}' and r.org_id is null;
            """);

        return user;
    }

    /// <summary>An active quarantine entry for <paramref name="caseId"/>, owned by the admin's membership.</summary>
    private async Task QuarantineAsync(World world, Guid caseId)
    {
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.quarantine_entry (id, org_id, project_id, case_id, owner, reason, state, expires_at)
            select gen_random_uuid(), '{world.Org}', '{world.Project}', '{caseId}', m.id, 'flaky', 'active', now() + interval '1 hour'
              from public.membership m
             where m.org_id = '{world.Org}' and m.user_id = '{world.User}';
            """);
    }

    /// <summary>
    /// A real catalog case of the project. <c>quarantine_entry.case_id</c> is a composite FK to
    /// <c>public.test_case (org_id, id)</c> (migration 0030:106) — there is no <c>case_revision</c> table and the
    /// tenancy fixture seeds no catalog — so the row is written here, as the owner, before the quarantine insert.
    /// </summary>
    private async Task<Guid> CaseIdAsync(World world)
    {
        var caseId = Guid.CreateVersion7();

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            """
            insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
            values (@id, @org_id, @project_id, @annotation_id, @name, 'playwright', 'abcdef0')
            """,
            ("id", caseId),
            ("org_id", world.Org),
            ("project_id", world.Project),
            ("annotation_id", Guid.CreateVersion7()),
            ("name", "checkout case"))).ShouldBe(1);
        await owner.CommitAsync();

        return caseId;
    }

    [Fact]
    public async Task Activate_FirstRevision_Is200_ActivatesIt_AndWritesOneAuditRow()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);

        var activated = await ActivateAsync(world, revision);

        activated.Status.ShouldBe(HttpStatusCode.OK);
        activated.Body.GetProperty("project_id").GetGuid().ShouldBe(world.Project);
        activated.Body.GetProperty("policy_revision_id").GetGuid().ShouldBe(revision);
        activated.Body.GetProperty("mode").GetString().ShouldBe("advisory");
        activated.Body.GetProperty("changed").GetBoolean().ShouldBeTrue();
        activated.Body.GetProperty("previous_policy_revision_id").ValueKind.ShouldBe(JsonValueKind.Null);
        activated.Body.GetProperty("hash").GetString().ShouldStartWith("sha256:");

        (await StateAsync(world, revision)).ShouldBe("active");
        (await PointerAsync(world)).ShouldBe(revision);
        (await ActivationAuditRowsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Activate_SecondRevision_RetiresTheFirst_KeepsOneActive_AndOneAuditRowPerCall()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, BodyA);
        var second = await CreateRevisionAsync(world, BodyB);

        (await ActivateAsync(world, first)).Status.ShouldBe(HttpStatusCode.OK);
        var activated = await ActivateAsync(world, second);

        activated.Status.ShouldBe(HttpStatusCode.OK);
        activated.Body.GetProperty("previous_policy_revision_id").GetGuid().ShouldBe(first);
        activated.Body.GetProperty("changed").GetBoolean().ShouldBeTrue();

        (await StateAsync(world, first)).ShouldBe("retired");
        (await StateAsync(world, second)).ShouldBe("active");
        (await ActiveCountAsync(world)).ShouldBe(1);
        (await PointerAsync(world)).ShouldBe(second);
        (await ActivationAuditRowsAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task Activate_TheAlreadyActiveRevision_Is200ChangedFalse_AndStillOneAuditRow()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);

        (await ActivateAsync(world, revision)).Status.ShouldBe(HttpStatusCode.OK);
        var again = await ActivateAsync(world, revision);

        again.Status.ShouldBe(HttpStatusCode.OK);
        again.Body.GetProperty("changed").GetBoolean().ShouldBeFalse();
        again.Body.GetProperty("previous_policy_revision_id").ValueKind.ShouldBe(JsonValueKind.Null);
        (await StateAsync(world, revision)).ShouldBe("active");
        (await ActivationAuditRowsAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task Activate_ARetiredRevision_Is409_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, BodyA);
        var second = await CreateRevisionAsync(world, BodyB);

        (await ActivateAsync(world, first)).Status.ShouldBe(HttpStatusCode.OK);
        (await ActivateAsync(world, second)).Status.ShouldBe(HttpStatusCode.OK);

        var refused = await ActivateAsync(world, first);

        refused.Status.ShouldBe(HttpStatusCode.Conflict);
        refused.Body.GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid_transition");
        (await StateAsync(world, second)).ShouldBe("active");
        (await PointerAsync(world)).ShouldBe(second);
        (await ActivationAuditRowsAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task Activate_UnknownOrAnotherProjectsRevision_Is404_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("other"));

        (await ActivateAsync(world, Guid.CreateVersion7())).Status.ShouldBe(HttpStatusCode.NotFound);
        (await ActivateAsync(world, revision, project: otherProject)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await ActivateAsync(world, revision, project: Guid.CreateVersion7())).Status.ShouldBe(HttpStatusCode.NotFound);

        (await StateAsync(world, revision)).ShouldBe("draft");
        (await ActivationAuditRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Activate_EnforcingModeAndMalformedBodies_Are422_AndWriteNothing()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);

        var enforcing = await ActivateAsync(
            world,
            revision,
            rawBody: $$"""{"policy_revision_id":"{{revision}}","mode":"enforcing"}""");
        var missing = await ActivateAsync(world, revision, rawBody: "{}");
        var garbage = await ActivateAsync(world, revision, rawBody: "not json");
        var unknown = await ActivateAsync(
            world,
            revision,
            rawBody: $$"""{"policy_revision_id":"{{revision}}","mode":"advisory","extra":1}""");

        enforcing.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        missing.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        garbage.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        unknown.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await StateAsync(world, revision)).ShouldBe("draft");
        (await ActivationAuditRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Activate_NeedsPolicyWrite_ViewerAndQaEngineerAreRefused()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);
        var viewer = await AddMemberAsync(world, "viewer");
        var qa = await AddMemberAsync(world, "qa_engineer");

        var asViewer = await ActivateAsync(world, revision, caller: TenancyApiFactory.Resolved(user: viewer, orgHeader: world.Org));
        var asQa = await ActivateAsync(world, revision, caller: TenancyApiFactory.Resolved(user: qa, orgHeader: world.Org));

        asViewer.Status.ShouldBe(HttpStatusCode.Forbidden);
        asQa.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await StateAsync(world, revision)).ShouldBe("draft");
        (await ActivationAuditRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Activate_ACriticalCaseUnderQuarantine_Is422_ListsTheCaseId_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var caseId = await CaseIdAsync(world);
        var revision = await CreateRevisionAsync(
            world,
            $$"""{"flaky_budget":3,"critical_cases":["{{caseId}}"]}""");
        await QuarantineAsync(world, caseId);

        var refused = await ActivateAsync(world, revision);

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.Body.GetProperty("error").GetProperty("code").GetString().ShouldBe("critical_case_quarantined");
        refused.Body.GetProperty("error").GetProperty("details")[0].GetProperty("path").GetString()
            .ShouldBe($"critical_cases/{caseId}");
        (await StateAsync(world, revision)).ShouldBe("draft");
        (await ActivationAuditRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Activate_ANonUuidCriticalCase_Is422_WithAPointer_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, """{"flaky_budget":3,"critical_cases":["not-a-uuid"]}""");

        var refused = await ActivateAsync(world, revision);

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.Body.GetProperty("error").GetProperty("details")[0].GetProperty("path").GetString()
            .ShouldBe("critical_cases/0");
        (await StateAsync(world, revision)).ShouldBe("draft");
        (await ActivationAuditRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Activate_CrossTenant_Is404_AndTheVictimsPointerDoesNotMove()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);
        (await ActivateAsync(world, revision)).Status.ShouldBe(HttpStatusCode.OK);

        // A second org's admin: the same project id and the same revision id are the same 404 (RLS scopes both reads).
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("other-org"));
        var outsider = TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg);

        var refused = await ActivateAsync(world, revision, caller: outsider);

        refused.Status.ShouldBe(HttpStatusCode.NotFound);
        (await PointerAsync(world)).ShouldBe(revision);
        (await ActivationAuditRowsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Activate_MissingIdempotencyKey_Is400_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);

        var refused = await ActivateAsync(world, revision, omitIdempotencyKey: true);

        refused.Status.ShouldBe(HttpStatusCode.BadRequest);
        refused.ErrorCode.ShouldBe("request_invalid");
        (await StateAsync(world, revision)).ShouldBe("draft");
        (await ActivationAuditRowsAsync(world)).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.policy_activation where org_id = '{world.Org:D}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Activate_SameKeySameBody_ReplaysTheResponse_AndWritesNoSecondAuditRow()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);
        var key = NewKey();

        var first = await ActivateAsync(world, revision, idempotencyKey: key);
        var replay = await ActivateAsync(world, revision, idempotencyKey: key);

        first.Status.ShouldBe(HttpStatusCode.OK);
        replay.Status.ShouldBe(HttpStatusCode.OK);
        // The stored response is revived verbatim, so the replayed body is byte-identical and the pointer did not move twice.
        replay.Body.GetRawText().ShouldBe(first.Body.GetRawText());
        (await StateAsync(world, revision)).ShouldBe("active");
        (await PointerAsync(world)).ShouldBe(revision);
        (await ActivationAuditRowsAsync(world)).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.idempotency_record where operation = 'policy:activate' and org_id = '{world.Org:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Activate_SameKeyDifferentBody_Is409_AndTheActiveRevisionIsUnchanged()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, BodyA);
        var second = await CreateRevisionAsync(world, BodyB);
        var key = NewKey();

        (await ActivateAsync(world, first, idempotencyKey: key)).Status.ShouldBe(HttpStatusCode.OK);

        var conflict = await ActivateAsync(world, second, idempotencyKey: key);

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await StateAsync(world, first)).ShouldBe("active");
        (await StateAsync(world, second)).ShouldBe("draft");
        (await PointerAsync(world)).ShouldBe(first);
        (await ActivationAuditRowsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Activate_AProjectScopedServiceCredential_Is403_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);

        // policy:write is outside the service-role ceiling, so a project-scoped CI credential cannot activate a
        // policy: the C2 stage refuses the token as malformed with 403 forbidden before the handler runs. The
        // minting helper is the one PolicyEndpointTests.ProjectScopedServiceCredential_IsRefusedByTheServiceRoleCeiling uses.
        var service = TenancyApiFactory.Resolved(
            serviceOrg: world.Org,
            serviceProject: world.Project,
            servicePermissions: ["policy:write"]);

        var refused = await ActivateAsync(world, revision, caller: service);

        refused.Status.ShouldBe(HttpStatusCode.Forbidden);
        refused.ErrorCode.ShouldBe("forbidden");
        (await StateAsync(world, revision)).ShouldBe("draft");
        (await ActivationAuditRowsAsync(world)).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.policy_activation where org_id = '{world.Org:D}'")).ShouldBe(0);
    }
}
