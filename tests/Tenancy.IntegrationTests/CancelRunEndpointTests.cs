// 05.4.1-T1 through the real host and a real PostgreSQL: the durable cancel request behind
// POST /v1/runs/{r}/cancel — one request row per run, the immediate canceled transition through 05.3.1's
// guard, the terminal no-op, and the isolation answers the story's security row names.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.4.1-T1 (AC1–AC4, AC6, AC8): <c>POST /v1/runs/{r}/cancel</c> records one durable
/// <c>run_cancel_request</c> row per run and moves a non-terminal run to <c>canceled</c> through the 05.3.1
/// transition guard. Because the row is unique per run and the state change goes through that guard, a
/// repeated, late or racing cancel can produce neither a second row nor a resurrected terminal run.
/// </summary>
[Trait("Category", "Docker")]
public sealed class CancelRunEndpointTests
{
    /// <summary>The outbox task type an applied transition writes (00 §5.3, 05.3.1 §7).</summary>
    private const string StateChangedTask = "orchestrator.run.state_changed";

    /// <summary>The audit action an applied transition writes (00 §7, 05.3.1 §7).</summary>
    private const string StateChangedAction = "run.state_changed";

    private readonly PostgresTenancyFixture _fixture;

    public CancelRunEndpointTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ AC1: accepted, recorded, canceled

    /// <summary>
    /// AC1: a <c>queued</c> run accepts the cancel with an empty <c>202</c>, stores exactly one
    /// <c>run_cancel_request</c> row (requester, reason, ordered deadlines, the local <c>requested</c> state) and
    /// moves the run to <c>canceled</c> — the transition's own outbox row and the caller-credited audit entry
    /// exist in the same commit.
    /// </summary>
    [Fact]
    public async Task CancelRun_Accepted_202_RequestRowPersisted()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        var answer = await CancelAsync(world, runId, reason: "stuck shard on checkout");

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        answer.Body.EnumerateObject().ShouldBeEmpty("the route answers 202 with no body (contracts/openapi.yaml)");

        (await StateAsync(world, runId)).ShouldBe("canceled");
        (await VersionAsync(world, runId)).ShouldBe(1, "queued -> canceled is one applied transition");
        (await OutboxTargetsAsync(world, runId)).ShouldBe(["canceled"], "the cancel is the transition the outbox reports");

        (await RowCountAsync(world, runId)).ShouldBe(1);

        var row = await RowAsync(world, runId);
        row.RequestedBy.ShouldBe(world.User, "requested_by is the resolved caller, never a body field");
        row.Reason.ShouldBe("stuck shard on checkout");
        row.State.ShouldBe("requested", "the execution stop has its own sub-state machine (plan §6)");
        row.SoftStopDeadlineAt.ShouldBeGreaterThanOrEqualTo(row.RequestedAt);
        row.KillAt.ShouldBeGreaterThanOrEqualTo(row.SoftStopDeadlineAt, "the kill never precedes the soft-stop deadline");
        row.RequestedAt.ShouldBeGreaterThan(
            DateTimeOffset.UtcNow.AddMinutes(-5), "requested_at is the server's clock, not a constant");

        var actor = await _fixture.ScalarAsync<string>(
            world.Database,
            $"select actor_type || ':' || actor_id from public.audit_log "
            + $"where org_id = '{world.Org}' and action = '{StateChangedAction}' and object_type = 'run' "
            + $"and object_id = '{runId}'");
        actor.ShouldBe($"user:{world.User:D}", "a cancel is a caller-driven edge, credited to its caller (05.3.1 AC5)");
    }

    // ------------------------------------------------------------------ AC2: terminal runs and repeats are no-ops

    /// <summary>
    /// AC2: a cancel on a run that already reached a terminal state answers the same empty <c>202</c> and writes
    /// nothing — no request row, no transition, no version bump — whichever terminal state it is.
    /// </summary>
    [Theory]
    [InlineData("complete")]
    [InlineData("incomplete")]
    [InlineData("canceled")]
    public async Task CancelRun_AlreadyTerminal_NoOp202(string terminal)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await RunInStateAsync(world, terminal);

        var version = await VersionAsync(world, runId);
        var outbox = await OutboxTargetsAsync(world, runId);

        var answer = await CancelAsync(world, runId, reason: "late cancel");

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        answer.Body.EnumerateObject().ShouldBeEmpty();
        (await StateAsync(world, runId)).ShouldBe(terminal, "a terminal run stays in its terminal state");
        (await VersionAsync(world, runId)).ShouldBe(version, "a no-op writes nothing, so it cannot bump the version");
        (await OutboxTargetsAsync(world, runId)).ShouldBe(outbox);
        (await RowCountAsync(world, runId)).ShouldBe(0, "a terminal run never gets a cancel request row");
    }

    /// <summary>
    /// AC2/§8 idempotency: three accepted cancels of one non-terminal run leave exactly one request row and one
    /// applied transition — the unique <c>(org_id, run_id)</c> key is what makes the repeat a no-op, so distinct
    /// idempotency keys are used on purpose (the row, not the key cache, is the arbiter).
    /// </summary>
    [Fact]
    public async Task CancelRun_Repeated_NoDuplicateRequestRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var answer = await CancelAsync(world, runId, reason: $"attempt {attempt}");

            answer.Status.ShouldBe(HttpStatusCode.Accepted, $"attempt {attempt}: {answer.ErrorMessage}");
        }

        (await RowCountAsync(world, runId)).ShouldBe(1);
        (await StateAsync(world, runId)).ShouldBe("canceled");
        (await VersionAsync(world, runId)).ShouldBe(1, "only the first cancel applied a transition");
        (await OutboxTargetsAsync(world, runId)).ShouldBe(["canceled"]);
    }

    /// <summary>
    /// §8 concurrency: six simultaneous cancels of one queued run all answer <c>202</c> and leave one row, one
    /// transition and no server error — the unique key and the transition's row lock decide the winner, never a
    /// check-then-insert race in the handler.
    /// </summary>
    [Fact]
    public async Task CancelRun_Concurrent_OneRow_No500()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        var answers = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(index => CancelAsync(world, runId, reason: $"concurrent {index}")));

        foreach (var answer in answers)
        {
            answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        }

        (await RowCountAsync(world, runId)).ShouldBe(1);
        (await StateAsync(world, runId)).ShouldBe("canceled");
        (await VersionAsync(world, runId)).ShouldBe(1, "one applied transition, whatever order the requests won in");
        (await OutboxTargetsAsync(world, runId)).ShouldBe(["canceled"]);
    }

    // ------------------------------------------------------------------ AC3/AC4: every non-terminal source state

    /// <summary>
    /// AC3: a run in <c>created</c> (the state no production writer inserts) or <c>queued</c> cancels straight
    /// to <c>canceled</c> — nothing is executing, so no soft-stop window is needed to reach the terminal state.
    /// </summary>
    [Theory]
    [InlineData("created")]
    [InlineData("queued")]
    public async Task CancelRun_FromCreatedOrQueued_StraightToCanceled(string from)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await RunInStateAsync(world, from);
        (await StateAsync(world, runId)).ShouldBe(from);

        var answer = await CancelAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        (await StateAsync(world, runId)).ShouldBe("canceled");
        (await VersionAsync(world, runId)).ShouldBe(1);
        (await RowCountAsync(world, runId)).ShouldBe(1);
    }

    /// <summary>
    /// AC4 (plan 05:115): a run in <c>running</c> or <c>collecting</c> becomes <c>canceled</c> as soon as the
    /// request is accepted — the soft-stop window acts on the execution, never on <c>run.state</c> — and the
    /// request row carries the deadlines the stop schedule uses.
    /// </summary>
    [Theory]
    [InlineData("running")]
    [InlineData("collecting")]
    public async Task CancelRun_FromRunningOrCollecting_TransitionsToCanceled(string from)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await RunInStateAsync(world, from);
        var version = await VersionAsync(world, runId);

        var answer = await CancelAsync(world, runId, reason: "operator stopped a hung run");

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        (await StateAsync(world, runId)).ShouldBe("canceled", "the cancel is recorded, not 'stopped' (plan 05:115)");
        (await VersionAsync(world, runId)).ShouldBe(version + 1);

        IReadOnlyList<string> expected = from == "running"
            ? new[] { "running", "canceled" }
            : new[] { "running", "collecting", "canceled" };
        (await OutboxTargetsAsync(world, runId)).ShouldBe(expected);
        (await RowCountAsync(world, runId)).ShouldBe(1);
        (await RowAsync(world, runId)).Reason.ShouldBe("operator stopped a hung run");
    }

    // ------------------------------------------------------------------ AC6: the reconciler race

    /// <summary>
    /// AC6: a reconciler verdict that runs after the cancel committed finds the run terminal and no-ops, so a
    /// canceled run never finishes <c>complete</c> and no second state change is written. The ordering is pinned
    /// because the HTTP handler owns its own commit boundary; the overlapping-transactions variant of the same
    /// race, where the verdict is provably parked on the cancel's row lock, is 05.3.1's
    /// <c>TransitionRun_CancelRaceWithReconcilerVerdict_StaysCanceled</c>.
    /// </summary>
    [Fact]
    public async Task CancelRun_RaceWithReconcilerComplete_StaysCanceled()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await RunInStateAsync(world, "collecting");

        var canceled = await CancelAsync(world, runId, reason: "cancel wins the race");
        canceled.Status.ShouldBe(HttpStatusCode.Accepted, canceled.ErrorMessage);
        var version = await VersionAsync(world, runId);

        var verdict = await TransitionAsync(world, runId, "complete");

        verdict.Outcome.ShouldBe(TransitionOutcome.NoOp, "the terminal no-op rule blocks the losing transition");
        verdict.State.ShouldBe("canceled");
        (await StateAsync(world, runId)).ShouldBe("canceled", "a canceled run never becomes complete");
        (await VersionAsync(world, runId)).ShouldBe(version);
        (await OutboxTargetsAsync(world, runId)).ShouldBe(["running", "collecting", "canceled"]);
        (await RowCountAsync(world, runId)).ShouldBe(1, "the losing verdict does not add a cancel row");

        // The run is terminal for good: a further verdict and a further cancel both stay no-ops.
        (await TransitionAsync(world, runId, "complete")).Outcome.ShouldBe(TransitionOutcome.NoOp);
        (await CancelAsync(world, runId)).Status.ShouldBe(HttpStatusCode.Accepted);
        (await RowCountAsync(world, runId)).ShouldBe(1);
        (await StateAsync(world, runId)).ShouldBe("canceled");
    }

    // ------------------------------------------------------------------ AC8: authorization and isolation

    /// <summary>
    /// AC8/§5.5 C2: <c>run:cancel</c> gates the route; a credential that does not hold it is refused <c>403</c>
    /// before the handler, so nothing is recorded and the run keeps its state. The accepted test above is this
    /// deny case's positive control.
    /// </summary>
    [Fact]
    public async Task CancelRun_NoPermission_403()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        var answer = await CancelAsync(
            world,
            runId,
            TenancyApiFactory.Resolved(
                serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["run:read"]));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("forbidden");
        (await StateAsync(world, runId)).ShouldBe("queued");
        (await RowCountAsync(world, runId)).ShouldBe(0, "a refused caller writes nothing");
    }

    /// <summary>
    /// AC8/00 §5.4: another tenant's run is the same <c>404 not_found</c> an unknown id gets — byte for byte, so
    /// the route is never an existence oracle — and no row is written for either attempt.
    /// </summary>
    [Fact]
    public async Task Cancel_CrossTenant_404()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var foreign = await CancelAsync(
            world,
            runId,
            TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg));
        var unknown = await CancelAsync(world, Guid.CreateVersion7());

        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
        foreign.ErrorCode.ShouldBe("not_found");
        foreign.ErrorCode.ShouldBe(unknown.ErrorCode);
        foreign.ErrorMessage.ShouldBe(
            unknown.ErrorMessage,
            "an existence oracle is a leak even when only the code matches");
        (await StateAsync(world, runId)).ShouldBe("queued", "another tenant's cancel changes nothing");
        (await RowCountAsync(world, runId)).ShouldBe(0);
    }

    /// <summary>
    /// AC8: a request tenant scope narrowed to a sibling project of the same org gets the shared <c>404
    /// not_found</c> for this project's run — the handler's scope check (<c>OrchestratorScope.Project</c>) is
    /// mapped to that 404, never to <c>project_scope_mismatch</c> — and the body an unknown id gets.
    /// </summary>
    [Fact]
    public async Task Cancel_CrossProject_404SharedNotFoundBody()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "other");

        // A member session scoped to the sibling project, the shape a project-scoped web URL publishes: the
        // caller itself holds run:cancel, so only the project scope can refuse the request.
        await using var host = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database),
            world.Org,
            sibling,
            RequestPrincipal.Member(world.User));
        using var scoped = host.CreateClient();
        var admin = TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org);

        var foreign = await CancelAsync(world, runId, admin, client: scoped);
        var unknown = await CancelAsync(world, Guid.CreateVersion7(), admin, client: scoped);

        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
        foreign.ErrorCode.ShouldBe("not_found");
        foreign.ErrorCode.ShouldBe(unknown.ErrorCode);
        foreign.ErrorMessage.ShouldBe(unknown.ErrorMessage);
        (await StateAsync(world, runId)).ShouldBe("queued");
        (await RowCountAsync(world, runId)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ §5: the optional, bounded, closed body

    /// <summary>
    /// §5: the body is optional — no body at all is "no reason", not a refusal, and the row records the absence
    /// as SQL NULL so a reader never has to tell an empty string from "none".
    /// </summary>
    [Fact]
    public async Task CancelRun_AbsentBody_AcceptedWithNoReason()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        var answer = await CancelAsync(world, runId, reason: null);

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        (await StateAsync(world, runId)).ShouldBe("canceled");
        (await RowCountAsync(world, runId)).ShouldBe(1);
        (await RowAsync(world, runId)).Reason.ShouldBeNull("absent means 'no reason', stored as NULL");
    }

    /// <summary>§5: an empty object and an explicit <c>null</c> member mean the same as an absent body.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"reason":null}""")]
    public async Task CancelRun_NoReasonMember_AcceptedWithNoReason(string rawBody)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        var answer = await CancelRawAsync(world.Client, runId, rawBody, world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        (await StateAsync(world, runId)).ShouldBe("canceled");
        var row = await RowAsync(world, runId);
        row.Reason.ShouldBeNull();
        row.RequestedBy.ShouldBe(world.User);
    }

    /// <summary>
    /// §5 (recorded in <c>CancelRunRequest</c>): a blank reason is refused rather than stored, because "no reason"
    /// already has its own spelling — the refusal names the member, and nothing is recorded.
    /// </summary>
    [Fact]
    public async Task CancelRun_BlankReason_422InvalidValue()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        var answer = await CancelRawAsync(world.Client, runId, """{"reason":"   "}""", world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid".Equals(answer.ErrorCode) ? "request_invalid" : "invalid_value");
        var detail = Details(answer).ShouldHaveSingleItem();
        detail.GetProperty("path").GetString().ShouldBe("/reason");
        detail.GetProperty("reason").GetString().ShouldBe("invalid_value");
        (await StateAsync(world, runId)).ShouldBe("queued");
        (await RowCountAsync(world, runId)).ShouldBe(0);
    }

    /// <summary>§5: the reason is bounded at 1000 characters; one more is refused before any statement runs.</summary>
    [Fact]
    public async Task CancelRun_ReasonTooLong_422TooLong()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        var answer = await CancelRawAsync(
            world.Client, runId, $$"""{"reason":"{{new string('r', 1001)}}"}""", world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var detail = Details(answer).ShouldHaveSingleItem();
        detail.GetProperty("path").GetString().ShouldBe("/reason");
        detail.GetProperty("reason").GetString().ShouldBe("too_long");
        (await StateAsync(world, runId)).ShouldBe("queued");
        (await RowCountAsync(world, runId)).ShouldBe(0);
    }

    /// <summary>
    /// §5 closed member set (00 §9.3): an unknown member is 422 <c>additional_properties</c> naming it, and a body
    /// that is not a JSON object is 422 <c>request_invalid</c> — a cancel never guesses what a caller meant.
    /// </summary>
    [Theory]
    [InlineData("""{"why":"because"}""", "/why", "additional_properties")]
    [InlineData("[]", "", "request_invalid")]
    [InlineData("not json", "", "request_invalid")]
    public async Task CancelRun_UnusableBody_422NothingRecorded(string rawBody, string path, string reason)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        var answer = await CancelRawAsync(world.Client, runId, rawBody, world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var detail = Details(answer).ShouldHaveSingleItem();
        detail.GetProperty("path").GetString().ShouldBe(path);
        detail.GetProperty("reason").GetString().ShouldBe(reason);
        (await StateAsync(world, runId)).ShouldBe("queued");
        (await RowCountAsync(world, runId)).ShouldBe(0);
    }

    /// <summary>
    /// §5: the transport caps the body at 4 KiB before it is buffered whole — a 1000-character reason fits, a body
    /// that is over the edge answers 413 <c>payload_too_large</c> without parsing and records nothing.
    /// </summary>
    [Fact]
    public async Task CancelRun_BodyOverEdgeCap_413PayloadTooLarge()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        var answer = await CancelRawAsync(
            world.Client, runId, $$"""{"reason":"{{new string('r', 5000)}}"}""", world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        answer.ErrorCode.ShouldBe("payload_too_large");
        (await StateAsync(world, runId)).ShouldBe("queued");
        (await RowCountAsync(world, runId)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The 00 §9.2 <c>error.details</c> rows of a refusal body.</summary>
    private static List<JsonElement> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()];

    /// <summary>One raw-body <c>POST /v1/runs/{r}/cancel</c> with a fresh idempotency key, black-box over HTTP.</summary>
    private static async Task<ApiAnswer> CancelRawAsync(
        HttpClient client,
        Guid runId,
        string rawBody,
        Action<HttpRequestHeaders> caller)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/runs/{runId:D}/cancel");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", NewKey());
        request.Content = new StringContent(rawBody, System.Text.Encoding.UTF8, "application/json");
        caller(request.Headers);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(IsJson(text) ? text : "{}");

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>One <c>POST /v1/runs/{r}/cancel</c> with a fresh idempotency key (the route requires one).</summary>
    private static Task<ApiAnswer> CancelAsync(
        RunCreationTests.World world,
        Guid runId,
        Action<HttpRequestHeaders>? caller = null,
        string? reason = "probe",
        HttpClient? client = null) =>
        (client ?? world.Client).SendAsync(
            HttpMethod.Post,
            $"/v1/runs/{runId}/cancel",
            caller ?? world.Admin,
            reason is null ? null : new { reason },
            NewKey());

    /// <summary>One API-created run (state <c>queued</c>, version 0).</summary>
    private static async Task<Guid> CreateQueuedRunAsync(RunCreationTests.World world)
    {
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        return created.Id("run_id");
    }

    /// <summary>One committed run in <paramref name="state"/>, driven there through the transition itself.</summary>
    private async Task<Guid> RunInStateAsync(RunCreationTests.World world, string state)
    {
        if (state == "created")
        {
            // The one state the API never writes (05.2.1-T4 inserts `queued`): seeded as the migration role.
            var seeded = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);
            (await StateAsync(world, seeded)).ShouldBe("created");

            return seeded;
        }

        var runId = await CreateQueuedRunAsync(world);
        foreach (var step in DriveTo(state))
        {
            (await TransitionAsync(world, runId, step)).State.ShouldBe(step);
        }

        (await StateAsync(world, runId)).ShouldBe(state);

        return runId;
    }

    /// <summary>The committed transitions that drive a fresh run from <c>queued</c> to <paramref name="state"/>.</summary>
    private static IEnumerable<string> DriveTo(string state) => state switch
    {
        "created" or "queued" => [],
        "running" => ["running"],
        "collecting" => ["running", "collecting"],
        "complete" => ["running", "collecting", "complete"],
        "incomplete" => ["running", "incomplete"],
        "canceled" => ["canceled"],
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "not a run state"),
    };

    /// <summary>
    /// One committed transition in its own tenant transaction — the reconciler's own path (05.3.2 hands the
    /// verdict to this same handler), so the race is decided by the production guard, not a test copy.
    /// </summary>
    private static async Task<TransitionResult> TransitionAsync(
        RunCreationTests.World world,
        Guid runId,
        string to,
        TransitionActor actor = TransitionActor.System)
    {
        await using var scope = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project);
        var result = await scope.TransitionAsync(new TransitionRunStateCommand(runId, to, actor));
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        if (result.Value.Outcome == TransitionOutcome.Applied)
        {
            await scope.FlushAuditAsync();
            await scope.CommitAsync();
        }

        return result.Value;
    }

    private Task<string> StateAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ScalarAsync<string>(world.Database, $"select state from public.run where id = '{runId}'");

    private Task<int> VersionAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ScalarAsync<int>(world.Database, $"select version from public.run where id = '{runId}'");

    /// <summary>The cancel request rows of one run.</summary>
    private Task<long> RowCountAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run_cancel_request where org_id = '{world.Org}' and run_id = '{runId}'");

    /// <summary>The one cancel request row of one run, as the table stores it.</summary>
    private async Task<CancelRow> RowAsync(RunCreationTests.World world, Guid runId)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(world.Database);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select requested_by, reason, state, requested_at, soft_stop_deadline_at, kill_at
              from public.run_cancel_request
             where org_id = @org_id and run_id = @run_id
            """;
        command.Parameters.AddWithValue("org_id", world.Org);
        command.Parameters.AddWithValue("run_id", runId);

        var token = TestContext.Current.CancellationToken;
        await using var reader = await command.ExecuteReaderAsync(token);
        (await reader.ReadAsync(token)).ShouldBeTrue($"no run_cancel_request row for run {runId:D}");
        (await reader.ReadAsync(token)).ShouldBeFalse("one run has at most one cancel request row");

        return new CancelRow(
            await reader.GetFieldValueAsync<Guid>(0, token),
            await reader.IsDBNullAsync(1, token) ? null : await reader.GetFieldValueAsync<string>(1, token),
            await reader.GetFieldValueAsync<string>(2, token),
            await reader.GetFieldValueAsync<DateTimeOffset>(3, token),
            await reader.GetFieldValueAsync<DateTimeOffset>(4, token),
            await reader.GetFieldValueAsync<DateTimeOffset>(5, token));
    }

    /// <summary>The <c>to</c> of every committed <c>run.state_changed</c> outbox row of one run, in order.</summary>
    private Task<IReadOnlyList<string>> OutboxTargetsAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ColumnAsync<string>(
            world.Database,
            $"select payload ->> 'to' from public.outbox where aggregate_id = '{runId}' and task_type = '{StateChangedTask}' "
            + "order by created_at, id");

    /// <summary>One stored cancel request, the way the table decides it.</summary>
    private sealed record CancelRow(
        Guid RequestedBy,
        string? Reason,
        string State,
        DateTimeOffset RequestedAt,
        DateTimeOffset SoftStopDeadlineAt,
        DateTimeOffset KillAt);
}
