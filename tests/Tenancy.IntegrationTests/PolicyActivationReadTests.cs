using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// F18 through the real host and a real PostgreSQL: <c>GET /v1/projects/{p}/policy-activation</c> reads the
/// project's stored pointer (never <c>previous_policy_revision_id</c>/<c>changed</c>, which describe one POST),
/// and <c>GET /v1/projects/{p}/policies/head</c> reads the appendix's newest revision whatever its state, with
/// the revision id as its <c>ETag</c>.
/// </summary>
/// <remarks>
/// The two reads are project-scoped, so every "not there" case is one answer: a project that was never
/// activated (or has no revision yet), an unknown id, a soft-deleted project (with or without a stored pointer),
/// another tenant's project (activated or not) and a pointer naming another project's revision all answer the
/// same <c>404</c> body, and the tests compare the error bodies rather than only the status. Every <c>403</c>
/// here is the permission stage's: <c>policy:read</c> is outside the service-role ceiling, so a service
/// credential is refused <c>forbidden</c> whether or not it holds <c>policy:read</c> and whatever project it is
/// scoped to — the ceiling answers before the <c>ProjectScope</c> guard, so neither the project in the path nor
/// the route changes the answer (the
/// <c>PolicyEndpointTests.ProjectScopedServiceCredential_IsRefusedByTheServiceRoleCeiling_OnReadAndWrite</c>
/// precedent).
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class PolicyActivationReadTests
{
    private const string BodyA = """{"flaky_budget":3,"required_suites":["smoke","regression"]}""";

    private const string BodyB = """{"flaky_budget":4,"required_suites":["smoke","regression"]}""";

    private const string BodyC = """{"flaky_budget":5,"required_suites":["smoke","regression"]}""";

    private const string BodyD = """{"flaky_budget":6,"required_suites":["smoke","regression"]}""";

    private const string HashShape = "^sha256:[0-9a-f]{64}$";

    private readonly PostgresTenancyFixture _fixture;

    public PolicyActivationReadTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
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
            """
            select action || '|' || object_type || '|' || object_id || '|' || coalesce(metadata ->> 'outcome', '-')
                   || '|' || coalesce(metadata ->> 'previous_id', '-') || '|' || coalesce(metadata ->> 'mode', '-')
                   || '|' || encode(after_hash, 'hex') || '|' || actor_type || '|' || actor_id
              from public.audit_log
             where action = 'policy.activate'
             order by seq
            """);

    private async Task<long> PointerCountAsync(World world) =>
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.policy_activation");

    // ------------------------------------------------------------------------------ GET policy-activation

    /// <summary>
    /// After a successful POST the read returns exactly what the POST wrote, with the read-only shape: the POST's
    /// <c>changed</c>/<c>previous_policy_revision_id</c> are absent because nothing stores them.
    /// </summary>
    [Fact]
    public async Task GetActivation_AfterActivation_ReturnsTheStoredPointer_WithoutThePostOnlyMembers()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);
        var activated = await ActivateAsync(world, revision.Id());
        activated.Status.ShouldBe(HttpStatusCode.OK, activated.ErrorMessage);

        var read = await GetAsync(world, ActivationUrl(world));

        read.Status.ShouldBe(HttpStatusCode.OK, read.ErrorMessage);
        read.Id("project_id").ShouldBe(world.Project);
        read.Id("policy_revision_id").ShouldBe(revision.Id());
        read.Text("mode").ShouldBe("advisory");
        read.Text("hash").ShouldBe(activated.Text("hash"));
        read.Text("hash")!.ShouldMatch(HashShape);
        read.Id("activated_by").ShouldBe(world.User);
        read.Body.GetProperty("activated_at").GetDateTimeOffset()
            .ShouldBe(activated.Body.GetProperty("activated_at").GetDateTimeOffset());

        MemberNames(read).ShouldBe(["activated_at", "activated_by", "hash", "mode", "policy_revision_id", "project_id"]);
        read.ETag.ShouldBeNull("the activation read issues no ETag");
    }

    /// <summary>
    /// The stored <c>enforcing</c> mode (unreachable through the API until 07.7.1) is readable, and an advisory
    /// re-activation of the already-active revision re-stamps the mode while the pointer does not move: the audit
    /// row says <c>outcome=unchanged</c> with <c>mode=advisory</c>, and a later activation of another revision is
    /// <c>outcome=changed</c> with the retired revision as <c>previous_id</c>.
    /// </summary>
    [Fact]
    public async Task GetActivation_SeededEnforcingMode_IsRead_AndAnAdvisoryReactivationRestampsTheMode_WithOneAuditRowPerCall()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, BodyA);
        var seededBy = Guid.CreateVersion7();
        await SeedEnforcingPointerAsync(world, first.Id(), seededBy);

        var enforcing = await GetAsync(world, ActivationUrl(world));
        enforcing.Status.ShouldBe(HttpStatusCode.OK, enforcing.ErrorMessage);
        enforcing.Id("policy_revision_id").ShouldBe(first.Id());
        enforcing.Text("mode").ShouldBe("enforcing");
        enforcing.Id("activated_by").ShouldBe(seededBy, "the seed's actor is read back as stored");

        // The API accepts only advisory, so this POST is the switch back; the target is already active.
        var back = await ActivateAsync(world, first.Id());
        back.Status.ShouldBe(HttpStatusCode.OK, back.ErrorMessage);
        back.Body.GetProperty("changed").GetBoolean().ShouldBeFalse();
        back.Text("mode").ShouldBe("advisory");

        var advisory = await GetAsync(world, ActivationUrl(world));
        advisory.Status.ShouldBe(HttpStatusCode.OK, advisory.ErrorMessage);
        advisory.Id("policy_revision_id").ShouldBe(first.Id());
        advisory.Text("mode").ShouldBe("advisory");
        advisory.Id("activated_by").ShouldBe(world.User);
        (await PointerCountAsync(world)).ShouldBe(1, "the pointer is one row per project, re-stamped");

        // A second, different revision moves the pointer: one more audit row, previous_id naming the retired one.
        var second = await CreateRevisionAsync(world, BodyB);
        var moved = await ActivateAsync(world, second.Id());
        moved.Status.ShouldBe(HttpStatusCode.OK, moved.ErrorMessage);
        moved.Body.GetProperty("changed").GetBoolean().ShouldBeTrue();
        moved.Body.GetProperty("previous_policy_revision_id").GetGuid().ShouldBe(first.Id());

        var hashOfFirst = first.Text("hash")!["sha256:".Length..];
        var hashOfSecond = second.Text("hash")!["sha256:".Length..];
        (await ActivationAuditRowsAsync(world)).ShouldBe(
        [
            $"policy.activate|policy_activation|{first.Id():D}|unchanged||advisory|{hashOfFirst}|user|{world.User:D}",
            $"policy.activate|policy_activation|{second.Id():D}|changed|{first.Id():D}|advisory|{hashOfSecond}|user|{world.User:D}",
        ]);

        var afterMove = await GetAsync(world, ActivationUrl(world));
        afterMove.Id("policy_revision_id").ShouldBe(second.Id());
        afterMove.Text("mode").ShouldBe("advisory");
        afterMove.Text("hash").ShouldBe(second.Text("hash"));
    }

    /// <summary>
    /// Every unreadable-project case is the same answer: a project with no pointer, an unknown id, a soft-deleted
    /// project, another tenant's project that <em>has</em> a live pointer, and a pointer naming another project's
    /// revision. Status and the error body (code, message, retryable, details) are compared, not only the status.
    /// </summary>
    [Fact]
    public async Task GetActivation_NeverActivatedUnknownSoftDeletedForeignAndCrossProjectPointer_AreTheSame404()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);

        // A same-org project whose pointer names the first project's revision (representable: the FK is org-scoped).
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "other");
        await SeedPointerAsync(world, sibling, revision.Id());

        var deleted = await CreateProjectAsync(world.Client, world.User, world.Org, "gone");
        await CreateRevisionAsync(world, BodyB, deleted);
        await _fixture.ExecuteAsync(world.Database, $"update public.project set status = 'deleted' where id = '{deleted}'");

        // Another tenant's project, activated by its own admin: the 404 must not be the never-activated answer.
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        var outsider = TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg);
        var rival = await CreateProjectAsync(world.Client, otherUser, otherOrg, "rival-checkout");
        var rivalRevision = await CreateRevisionAsync(world, BodyC, rival, outsider);
        (await ActivateAsync(world, rivalRevision.Id(), rival, outsider)).Status.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(world, ActivationUrl(world, rival), outsider)).Status.ShouldBe(HttpStatusCode.OK, "its own tenant reads the seeded pointer");

        var never = await GetAsync(world, ActivationUrl(world));
        var unknown = await GetAsync(world, ActivationUrl(world, Guid.CreateVersion7()));
        var hidden = await GetAsync(world, ActivationUrl(world, deleted));
        var foreign = await GetAsync(world, ActivationUrl(world, rival));
        var crossProject = await GetAsync(world, ActivationUrl(world, sibling));

        foreach (var answer in new[] { never, unknown, hidden, foreign, crossProject })
        {
            answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
            answer.ErrorCode.ShouldBe("not_found");
            answer.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        }

        var shapes = new[] { never, unknown, hidden, foreign, crossProject }.Select(ProblemShape).Distinct().ToList();
        shapes.Count.ShouldBe(1, "the five unreadable-project cases must share one error body: " + string.Join(" / ", shapes));

        // Both stored pointers are still there: the reads hid them, they did not delete them.
        (await PointerCountAsync(world)).ShouldBe(2);
    }

    /// <summary>
    /// A pointer that outlives its project's soft deletion is not readable: the read is the never-activated
    /// <c>404</c>, body included, so a deleted project is not an existence oracle for its own activation either.
    /// </summary>
    [Fact]
    public async Task GetActivation_SoftDeletedProjectWithALivePointer_IsTheSame404AsNeverActivated()
    {
        await using var world = await StartAsync();

        var deleted = await CreateProjectAsync(world.Client, world.User, world.Org, "gone");
        var revision = await CreateRevisionAsync(world, BodyA, deleted);
        (await ActivateAsync(world, revision.Id(), deleted)).Status.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(world, ActivationUrl(world, deleted))).Status.ShouldBe(HttpStatusCode.OK, "the pointer is live before the deletion");

        // The soft deletion is the status column the other cases use; the pointer row stays behind it.
        await _fixture.ExecuteAsync(world.Database, $"update public.project set status = 'deleted' where id = '{deleted}'");
        (await PointerCountAsync(world)).ShouldBe(1, "the soft deletion left the pointer row in place");

        var never = await GetAsync(world, ActivationUrl(world));
        var hidden = await GetAsync(world, ActivationUrl(world, deleted));

        hidden.Status.ShouldBe(HttpStatusCode.NotFound, hidden.ErrorMessage);
        hidden.ErrorCode.ShouldBe("not_found");
        hidden.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        ProblemShape(hidden).ShouldBe(ProblemShape(never));
    }

    /// <summary>
    /// The activation read follows the stored pointer, the head follows the append order: activating revision 2
    /// moves the pointer to 2, and appending revision 3 without activating it leaves the activation on 2 while the
    /// head is 3.
    /// </summary>
    [Fact]
    public async Task GetActivation_FollowsTheSecondActivation_WhileTheHeadMovesToTheUnactivatedNewest()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, BodyA);
        (await ActivateAsync(world, first.Id())).Status.ShouldBe(HttpStatusCode.OK);

        var second = await CreateRevisionAsync(world, BodyB);
        var moved = await ActivateAsync(world, second.Id());
        moved.Status.ShouldBe(HttpStatusCode.OK, moved.ErrorMessage);
        moved.Body.GetProperty("changed").GetBoolean().ShouldBeTrue();
        moved.Body.GetProperty("previous_policy_revision_id").GetGuid().ShouldBe(first.Id());

        var atSecond = await GetAsync(world, ActivationUrl(world));
        atSecond.Status.ShouldBe(HttpStatusCode.OK, atSecond.ErrorMessage);
        atSecond.Id("policy_revision_id").ShouldBe(second.Id());
        atSecond.Text("hash").ShouldBe(second.Text("hash"));
        atSecond.Text("mode").ShouldBe("advisory");

        var third = await CreateRevisionAsync(world, BodyC);

        var stillSecond = await GetAsync(world, ActivationUrl(world));
        stillSecond.Status.ShouldBe(HttpStatusCode.OK, stillSecond.ErrorMessage);
        stillSecond.Id("policy_revision_id").ShouldBe(second.Id(), "appending a revision does not move the pointer");
        stillSecond.Text("hash").ShouldBe(second.Text("hash"));

        var head = await GetAsync(world, HeadUrl(world));
        head.Status.ShouldBe(HttpStatusCode.OK, head.ErrorMessage);
        head.Id().ShouldBe(third.Id(), "the head is the newest revision, not the activated one");
        head.Body.GetProperty("revision_no").GetInt32().ShouldBe(3);
        head.Text("state").ShouldBe("draft");

        (await PointerCountAsync(world)).ShouldBe(1);
    }

    /// <summary>A credential without <c>policy:read</c> is refused 403 before the handler, for both reads.</summary>
    [Fact]
    public async Task GetActivation_AndHead_WithoutPolicyRead_Are403_NoExistenceOracle()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);
        (await ActivateAsync(world, revision.Id())).Status.ShouldBe(HttpStatusCode.OK);

        // No project scope: the answer must come from the permission stage alone. A project-scoped credential is
        // refused by the same service-role ceiling before its scope guard — the scope test below records that.
        var ci = TenancyApiFactory.Resolved(serviceOrg: world.Org, servicePermissions: ["run:create"]);

        var activation = await GetAsync(world, ActivationUrl(world), ci);
        var activationUnknown = await GetAsync(world, ActivationUrl(world, Guid.CreateVersion7()), ci);
        var head = await GetAsync(world, HeadUrl(world), ci);
        var headUnknown = await GetAsync(world, HeadUrl(world, Guid.CreateVersion7()), ci);

        foreach (var answer in new[] { activation, head })
        {
            answer.Status.ShouldBe(HttpStatusCode.Forbidden);
            answer.ErrorCode.ShouldBe("forbidden");
        }

        // The refusal is the permission stage's, so an existing and an unknown project are indistinguishable.
        ProblemShape(activation).ShouldBe(ProblemShape(activationUnknown));
        ProblemShape(head).ShouldBe(ProblemShape(headUnknown));
        ProblemShape(activation).ShouldBe(ProblemShape(head));
    }

    /// <summary>
    /// Contract case (6): <c>policy:read</c> is outside the service-role ceiling
    /// (<c>PermissionEvaluator.ServiceRoleCeiling</c>), so a service credential can never hold the route
    /// permission and the ceiling answers 403 <c>forbidden</c> before the <c>ProjectScope</c> guard runs. Both
    /// projects hold a live pointer, so a read that got past the ceiling would answer <c>200</c>: the refusal is the
    /// permission stage's on the credential's own project and on another alike, on both routes, with one body — so
    /// neither the project in the path nor the route is an existence oracle for a project-scoped credential.
    /// </summary>
    [Fact]
    public async Task GetActivation_AndHead_AProjectScopedPolicyReadCredential_Is403ForbiddenOnEveryProject()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);
        (await ActivateAsync(world, revision.Id())).Status.ShouldBe(HttpStatusCode.OK);

        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "other");
        var siblingRevision = await CreateRevisionAsync(world, BodyB, sibling);
        (await ActivateAsync(world, siblingRevision.Id(), sibling)).Status.ShouldBe(HttpStatusCode.OK);

        var scoped = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: sibling, servicePermissions: ["policy:read"]);

        var activationOther = await GetAsync(world, ActivationUrl(world), scoped);
        var activationOwn = await GetAsync(world, ActivationUrl(world, sibling), scoped);
        var headOther = await GetAsync(world, HeadUrl(world), scoped);
        var headOwn = await GetAsync(world, HeadUrl(world, sibling), scoped);

        foreach (var answer in new[] { activationOther, activationOwn, headOther, headOwn })
        {
            answer.Status.ShouldBe(HttpStatusCode.Forbidden, answer.ErrorMessage);
            answer.ErrorCode.ShouldBe(
                "forbidden",
                "policy:read is outside the service ceiling, so the ceiling answers before the project scope does");
        }

        var shapes = new[] { activationOther, activationOwn, headOther, headOwn }.Select(ProblemShape).Distinct().ToList();
        shapes.Count.ShouldBe(
            1,
            "the own project, another project and both routes must share one refusal: " + string.Join(" / ", shapes));
    }

    // ------------------------------------------------------------------------------------- GET policies/head

    /// <summary>The head is the newest <c>revision_no</c> row, and its own id is the <c>ETag</c> the detail route issues.</summary>
    [Fact]
    public async Task Head_ReturnsTheNewestRevision_WithItsIdAsTheEtag()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, BodyA);
        var second = await CreateRevisionAsync(world, BodyB);
        var third = await CreateRevisionAsync(world, BodyC);

        var head = await GetAsync(world, HeadUrl(world));

        head.Status.ShouldBe(HttpStatusCode.OK, head.ErrorMessage);
        head.Id().ShouldBe(third.Id());
        head.Body.GetProperty("revision_no").GetInt32().ShouldBe(3);
        head.Text("state").ShouldBe("draft");
        head.Text("hash").ShouldBe(third.Text("hash"));
        head.Id("created_by").ShouldBe(world.User);
        head.Body.GetProperty("version").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        head.ETag.ShouldBe($"\"{third.Id():D}\"");
        head.Body.TryGetProperty("body", out _).ShouldBeFalse("the summary carries no body");

        // The same tag the detail route issues for the very same row.
        var detail = await GetAsync(world, $"/v1/policies/{third.Id()}");
        detail.ETag.ShouldBe(head.ETag);
        detail.Id().ShouldBe(head.Id());

        // The head never folds to an older revision: the earlier ones are addressable, they are just not the head.
        first.Id().ShouldNotBe(head.Id());
        second.Id().ShouldNotBe(head.Id());
    }

    /// <summary>
    /// The head is the append-only sequence's last row, not the activated one: activating an older revision leaves
    /// the newest (draft) revision as the head, and a row that is newest but <c>retired</c> is still the head.
    /// </summary>
    [Fact]
    public async Task Head_IsTheNewestRevision_WhateverItsState_AndIgnoresTheActivationPointer()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, BodyA);
        var second = await CreateRevisionAsync(world, BodyB);
        var third = await CreateRevisionAsync(world, BodyC);

        (await ActivateAsync(world, first.Id())).Status.ShouldBe(HttpStatusCode.OK);

        var headWhileOlderIsActive = await GetAsync(world, HeadUrl(world));
        headWhileOlderIsActive.Status.ShouldBe(HttpStatusCode.OK, headWhileOlderIsActive.ErrorMessage);
        headWhileOlderIsActive.Id().ShouldBe(third.Id());
        headWhileOlderIsActive.Text("state").ShouldBe("draft");
        headWhileOlderIsActive.Body.GetProperty("revision_no").GetInt32().ShouldBe(3);
        headWhileOlderIsActive.Id().ShouldNotBe(first.Id(), "the activation pointer is not the head");

        // Activating the first revision retired nothing else: the two newer revisions keep their state.
        (await GetAsync(world, $"/v1/policies/{second.Id()}")).Text("state").ShouldBe("draft");
        (await GetAsync(world, $"/v1/policies/{first.Id()}")).Text("state").ShouldBe("active");

        // A newest revision of another project that has since been retired is still that project's head.
        var retiredProject = await CreateProjectAsync(world.Client, world.User, world.Org, "retired");
        var olderInRetiredProject = await CreateRevisionAsync(world, BodyD, retiredProject);
        var newestInRetiredProject = await CreateRevisionAsync(world, BodyC, retiredProject);
        (await ActivateAsync(world, newestInRetiredProject.Id(), retiredProject)).Status.ShouldBe(HttpStatusCode.OK);
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.policy_revision set state = 'retired' where id = '{newestInRetiredProject.Id()}'");

        var headOfRetired = await GetAsync(world, HeadUrl(world, retiredProject));
        headOfRetired.Status.ShouldBe(HttpStatusCode.OK, headOfRetired.ErrorMessage);
        headOfRetired.Id().ShouldBe(newestInRetiredProject.Id());
        headOfRetired.Text("state").ShouldBe("retired");
        headOfRetired.Body.GetProperty("revision_no").GetInt32().ShouldBe(2);
        headOfRetired.Id().ShouldNotBe(olderInRetiredProject.Id());

        // The first project's head is still the draft third revision.
        (await GetAsync(world, HeadUrl(world))).Id().ShouldBe(third.Id());
    }

    /// <summary>
    /// A project without revisions, an unknown id, a soft-deleted project and another tenant's project are one
    /// answer: the same <c>404</c> body.
    /// </summary>
    [Fact]
    public async Task Head_NoRevisionsUnknownSoftDeletedAndForeign_AreTheSame404()
    {
        await using var world = await StartAsync();
        await CreateRevisionAsync(world, BodyA);

        var empty = await CreateProjectAsync(world.Client, world.User, world.Org, "empty");

        var deleted = await CreateProjectAsync(world.Client, world.User, world.Org, "gone");
        await CreateRevisionAsync(world, BodyB, deleted);
        await _fixture.ExecuteAsync(world.Database, $"update public.project set status = 'deleted' where id = '{deleted}'");

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        var outsider = TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg);

        var noRevisions = await GetAsync(world, HeadUrl(world, empty));
        var unknown = await GetAsync(world, HeadUrl(world, Guid.CreateVersion7()));
        var hidden = await GetAsync(world, HeadUrl(world, deleted));
        var foreign = await GetAsync(world, HeadUrl(world), outsider);

        foreach (var answer in new[] { noRevisions, unknown, hidden, foreign })
        {
            answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
            answer.ErrorCode.ShouldBe("not_found");
            answer.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
            answer.ETag.ShouldBeNull("a refusal carries no ETag");
        }

        var shapes = new[] { noRevisions, unknown, hidden, foreign }.Select(ProblemShape).Distinct().ToList();
        shapes.Count.ShouldBe(1, "the four unreadable-project cases must share one error body: " + string.Join(" / ", shapes));
    }
}
