// Request bodies are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.1.1 AC4 / 07.1.1-T4.2 through the real host and a real PostgreSQL: run creation pins the project's active policy
/// revision (and the two values its body sets), a later activation never moves an existing run's pin, a project with no
/// active revision keeps the EVIDGATE:Runs defaults and a null pin, and a concurrent activation and run create do not
/// deadlock and pin a revision that was active at some commit. Spans this story's
/// <c>POST /v1/projects/{p}/policy-activation</c> and 05.2.1's run-create handler.
/// </summary>
/// <remarks>
/// The run world, the run-create call and the manifest seeding are reused from <see cref="RunCreationTests"/>, so the
/// pin is read from the same row the 05.2.1 suite writes.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class PolicyRunPinningTests
{
    /// <summary>A body the <c>policy-revision.v1</c> schema accepts with neither timing value set (both optional).</summary>
    private const string PolicyPlain = """{"required_suites":["smoke"],"flaky_budget":3}""";

    /// <summary>A different body (one value changed) — a second, distinct revision.</summary>
    private const string PolicyOther = """{"required_suites":["smoke"],"flaky_budget":4}""";

    private readonly PostgresTenancyFixture _fixture;

    public PolicyRunPinningTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>Creates a policy revision through <c>POST /v1/projects/{p}/policies</c> (07.1.1-T3) and returns its id.</summary>
    private static async Task<Guid> CreatePolicyAsync(
        RunCreationTests.World world,
        string body,
        Action<HttpRequestHeaders>? caller = null,
        Guid? project = null)
    {
        var answer = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{project ?? world.Project}/policies",
            caller ?? world.Admin,
            JsonNode.Parse(body));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);

        return answer.Id();
    }

    /// <summary>Activates a revision through <c>POST /v1/projects/{p}/policy-activation</c> (07.1.1-T4.1).</summary>
    private static Task<ApiAnswer> ActivateAsync(
        RunCreationTests.World world,
        Guid revisionId,
        Action<HttpRequestHeaders>? caller = null,
        Guid? project = null,
        string? mode = null)
    {
        object body = mode is null
            ? new { policy_revision_id = revisionId }
            : new { policy_revision_id = revisionId, mode };

        return world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{project ?? world.Project}/policy-activation",
            caller ?? world.Admin,
            body,
            NewKey());
    }

    /// <summary>Sends a raw JSON body verbatim, so a literal like <c>30.0</c> or <c>3e1</c> reaches the server unchanged.</summary>
    private static async Task<ApiAnswer> SendRawAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        Action<HttpRequestHeaders> caller,
        string raw)
    {
        using var request = new HttpRequestMessage(method, url);
        caller(request.Headers);
        request.Content = new StringContent(raw, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>The pin stored on one run.</summary>
    private Task<Guid?> RunPinAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ScalarAsync<Guid?>(world.Database, $"select policy_revision_id from public.run where id = '{runId}'");

    /// <summary>The offset of <c>collection_deadline_at</c> from <c>created_at</c>, in seconds (the DB clock, so exact).</summary>
    private Task<double> DeadlineOffsetAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ScalarAsync<double>(
            world.Database,
            $"select (extract(epoch from (collection_deadline_at - created_at)))::double precision from public.run where id = '{runId}'");

    private Task<int> LateWindowAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ScalarAsync<int>(world.Database, $"select late_window_minutes from public.run where id = '{runId}'");

    /// <summary>An active member of <paramref name="roleCode"/> in the org, seeded directly (the org founder is the admin).</summary>
    private async Task<Guid> AddMemberAsync(RunCreationTests.World world, string roleCode)
    {
        var user = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public."user" (id, email) values ('{user}', '{roleCode}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values (gen_random_uuid(), '{world.Org}', '{user}', '{roleCode}@example.test', 'active');
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), '{world.Org}', m.id, r.id
              from public.membership m, public.role r
             where m.org_id = '{world.Org}' and m.user_id = '{user}'
               and r.code = '{roleCode}' and r.org_id is null;
            """);

        return user;
    }

    private static Action<HttpRequestHeaders> As(Guid user, Guid org) =>
        TenancyApiFactory.Resolved(user: user, orgHeader: org);

    // ------------------------------------------------------------------ AC4: the pin

    /// <summary>
    /// 07.1.1 AC4: activating R1 pins a run created afterwards to R1; activating R2 later leaves that run on R1 and a
    /// run created after the second activation pins R2.
    /// </summary>
    [Fact]
    public async Task Activation_PinsTheRevision_AndALaterActivationDoesNotMoveTheRun()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var r1 = await CreatePolicyAsync(world, PolicyPlain);
        var activation = await ActivateAsync(world, r1, mode: "advisory");
        activation.Status.ShouldBe(HttpStatusCode.OK, activation.ErrorMessage);
        activation.Id("policy_revision_id").ShouldBe(r1);
        activation.Body.GetProperty("changed").GetBoolean().ShouldBeTrue();
        (await _fixture.ScalarAsync<Guid>(world.Database, "select policy_revision_id from public.policy_activation"))
            .ShouldBe(r1);

        var first = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        var firstRun = first.Id("run_id");
        (await RunPinAsync(world, firstRun)).ShouldBe(r1);

        // A second activation moves the project's pointer but never the existing run's pin.
        var r2 = await CreatePolicyAsync(world, PolicyOther);
        (await ActivateAsync(world, r2)).Status.ShouldBe(HttpStatusCode.OK);

        (await RunPinAsync(world, firstRun)).ShouldBe(r1);

        var second = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);
        (await RunPinAsync(world, second.Id("run_id"))).ShouldBe(r2);
    }

    /// <summary>A project with no policy revision at all keeps the defaults and a null pin (Q1).</summary>
    [Fact]
    public async Task NoActivePolicy_RunKeepsNullPinAndDefaults()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        var runId = answer.Id("run_id");

        (await RunPinAsync(world, runId)).ShouldBeNull();
        (await DeadlineOffsetAsync(world, runId)).ShouldBe(7200d);
        (await LateWindowAsync(world, runId)).ShouldBe(1440);
    }

    /// <summary>A revision that exists but is still a draft is not active, so nothing is pinned.</summary>
    [Fact]
    public async Task DraftRevisionWithoutActivation_DoesNotPin()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        await CreatePolicyAsync(world, PolicyPlain);

        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);

        (await RunPinAsync(world, answer.Id("run_id"))).ShouldBeNull();
    }

    /// <summary>The two values the pinned revision's body sets replace the defaults; an unset value keeps its default.</summary>
    [Fact]
    public async Task PolicyValues_ReplaceTheRunDefaults()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        // late_window_minutes 0 is valid (migration 0033 widens the run CHECK to 0..525600): no late evidence accepted.
        var timed = await CreatePolicyAsync(
            world,
            """{"collection_timeout_minutes":30,"late_window_minutes":0}""");
        (await ActivateAsync(world, timed)).Status.ShouldBe(HttpStatusCode.OK);

        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        var runId = answer.Id("run_id");

        (await RunPinAsync(world, runId)).ShouldBe(timed);
        (await DeadlineOffsetAsync(world, runId)).ShouldBe(1800d);
        (await LateWindowAsync(world, runId)).ShouldBe(0);

        // Only the collection timeout set: the late window falls back to the default.
        var partial = await CreatePolicyAsync(world, """{"collection_timeout_minutes":15}""");
        (await ActivateAsync(world, partial)).Status.ShouldBe(HttpStatusCode.OK);

        var second = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);

        (await DeadlineOffsetAsync(world, second.Id("run_id"))).ShouldBe(900d);
        (await LateWindowAsync(world, second.Id("run_id"))).ShouldBe(1440);
    }

    /// <summary>
    /// 07.1.1 AC4 / finding F1: a revision whose body writes an integer field with a scale or an exponent
    /// (<c>30.0</c>, <c>3e1</c>) is accepted by the create route and activated; the reader casts the stored
    /// <c>jsonb</c> through <c>numeric</c>, so <c>'30.0'::int</c> (SQLSTATE 22P02) can never turn a run create
    /// into a 500. The pin and the two values the body sets are read back from the run row.
    /// </summary>
    [Fact]
    public async Task FloatLiteralPolicyValues_AreAcceptedAndPinnedAsIntegers()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        // Sent verbatim: jsonb keeps the literal's scale, so the stored body still reads back "30.0".
        var created = await SendRawAsync(
            world.Client,
            HttpMethod.Post,
            $"/v1/projects/{world.Project}/policies",
            world.Admin,
            """{"collection_timeout_minutes":30.0,"late_window_minutes":3e1}""");
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var revision = created.Id();

        (await ActivateAsync(world, revision)).Status.ShouldBe(HttpStatusCode.OK);

        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        var runId = answer.Id("run_id");

        (await RunPinAsync(world, runId)).ShouldBe(revision);
        (await DeadlineOffsetAsync(world, runId)).ShouldBe(1800d);
        (await LateWindowAsync(world, runId)).ShouldBe(30);
    }

    /// <summary>An active revision of another project in the same org is never pinned (the pin is per project).</summary>
    [Fact]
    public async Task AnotherProjectsRevision_IsNeverPinned()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var other = await CreateProjectAsync(world.Client, world.User, world.Org, "other");

        // The other project has an active revision; this project has none.
        var foreign = await CreatePolicyAsync(world, PolicyPlain, project: other);
        (await ActivateAsync(world, foreign, project: other)).Status.ShouldBe(HttpStatusCode.OK);

        var unpinned = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        unpinned.Status.ShouldBe(HttpStatusCode.Created, unpinned.ErrorMessage);
        (await RunPinAsync(world, unpinned.Id("run_id"))).ShouldBeNull();

        // This project's own activation pins its own revision, not the other project's.
        var mine = await CreatePolicyAsync(world, PolicyOther);
        (await ActivateAsync(world, mine)).Status.ShouldBe(HttpStatusCode.OK);

        var pinned = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        pinned.Status.ShouldBe(HttpStatusCode.Created, pinned.ErrorMessage);
        (await RunPinAsync(world, pinned.Id("run_id"))).ShouldBe(mine);
    }

    /// <summary>
    /// A concurrent activation and run create do not deadlock or time out: the run-create read takes no project row
    /// lock (finding F4), so the activation's FOR NO KEY UPDATE never waits on it. Both succeed, the run pins a
    /// revision that was active at some commit (R1 or R2), and exactly one revision is active afterwards.
    /// </summary>
    [Fact]
    public async Task ConcurrentActivationAndRunCreate_PinIsARevisionThatWasActive()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var r1 = await CreatePolicyAsync(world, PolicyPlain);
        (await ActivateAsync(world, r1)).Status.ShouldBe(HttpStatusCode.OK);
        var r2 = await CreatePolicyAsync(world, PolicyOther);

        var activationTask = ActivateAsync(world, r2);
        var createTask = RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));

        var answers = await Task.WhenAll(activationTask, createTask)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var activation = answers[0];
        var create = answers[1];
        activation.Status.ShouldBe(HttpStatusCode.OK, activation.ErrorMessage);
        create.Status.ShouldBe(HttpStatusCode.Created, create.ErrorMessage);

        (await RunPinAsync(world, create.Id("run_id"))).ShouldBeOneOf(r1, r2);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.policy_revision where state = 'active'")).ShouldBe(1);
    }

    /// <summary>Only <c>release_approver</c> (policy:write) may activate; every other member role is refused 403.</summary>
    [Fact]
    public async Task Activation_OnlyReleaseApprover()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var revision = await CreatePolicyAsync(world, PolicyPlain);
        var qaEngineer = await AddMemberAsync(world, "qa_engineer");

        var denied = await ActivateAsync(world, revision, caller: As(qaEngineer, world.Org));
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);

        var granted = await ActivateAsync(world, revision, caller: world.Admin);
        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);
    }
}
