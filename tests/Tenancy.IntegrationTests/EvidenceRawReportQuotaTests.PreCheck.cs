// The advisory half of the report path's quota rule: a report the queue already deferred is asked about the run's
// project counter before its bytes are fetched, so a period with no room for one event refuses the attempt without a
// read, a parse or a write. The answer never admits work; a block writes the same stamp the transaction's block does,
// and a database fault of the read defers the attempt without spending one. The transactional gate still decides.
using System.Collections.Concurrent;
using System.Globalization;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportQuotaTests
{
    [Fact]
    public async Task A_Deferred_Report_With_No_Room_Is_Refused_Before_Its_Bytes_Are_Fetched()
    {
        var storage = new ReportStorageSpy();
        var gate = new ReportPreCheckGate();
        var world = await StartAsync(services =>
        {
            ReplaceObjectStorage(services, inner => storage.Wrap(inner));
            ReplaceQuotaGate(services, inner => gate.Wrap(inner));
        });
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 10m, limit: 10m);
        await SeedDeferredTaskAsync(world, artifactId);
        var before = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        var log = new CapturedLog();

        var outcome = await ParseAsync(world, artifactId, log);

        outcome.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        outcome.Reason.ShouldBe("quota_exceeded");
        outcome.Events.ShouldBe(0);
        outcome.RetryAfter.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));

        // The question the pre-check asked: the gate resolves the transaction's month itself, the run's project — the
        // counter the write transaction charges — and one event, the smallest batch, because a deferred report's real
        // count is unknown before it is parsed. The stamp request carries the month the answer named.
        var request = outcome.QuotaRequest.ShouldNotBeNull();
        request.Period.ShouldBe(await PeriodAsync(world), "the stamp names the month the gate decided in");
        request.ProjectId.ShouldBe(world.Project);
        request.Quantity.ShouldBe(1m);
        request.IncludeTenantScope.ShouldBeTrue();
        gate.PeekRequests.ShouldHaveSingleItem().Period.ShouldBeNull("the gate resolves the month in its own statement");

        storage.Gets.ShouldBe(0, "the counter's answer is known before the fetch, so no object is read");
        gate.Peeks.ShouldBe(1, "the deferred report is the one the read scope asks about");
        gate.Checks.ShouldBe(0, "a report stopped before the fetch never reaches the transactional gate");
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountAsync(world, "event_batch")).ShouldBe(0);
        (await CountRawReportsAsync(world, artifactId)).ShouldBe(0);
        (await RunStateAsync(world)).ShouldBe("queued");

        var after = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        after.Used.ShouldBe(before.Used);
        after.Version.ShouldBeGreaterThan(before.Version, "a pre-check block writes the stamp the rollback would lose");
        after.BlockedAt.ShouldNotBeNull("a pre-check block stamps the counter it refused");

        // Operators must be able to tell this deferral from one a rolled-back batch produced.
        log.Lines.ShouldContain(line => line.Contains("deferred before the fetch", StringComparison.Ordinal));
        log.Lines.ShouldNotContain(line => line.Contains("were not committed", StringComparison.Ordinal));

        // The task entry point defers the same row, for the gate's wait inside the queue's clamp.
        var deferred = await Should.ThrowAsync<TaskDeferredException>(
            async () => await CreateHandler(world).HandleAsync(
                new ParseRawReportPayload(world.RunId, artifactId, ProducerKind), world.Org, Token));
        deferred.Delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        deferred.Delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task A_Deferred_Report_With_Room_Is_Fetched_And_The_Transactional_Gate_Decides()
    {
        var storage = new ReportStorageSpy();
        var gate = new ReportPreCheckGate();
        var world = await StartAsync(services =>
        {
            ReplaceObjectStorage(services, inner => storage.Wrap(inner));
            ReplaceQuotaGate(services, inner => gate.Wrap(inner));
        });
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 0m, limit: 100m);
        await SeedDeferredTaskAsync(world, artifactId);

        var outcome = await ParseAsync(world, artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);
        storage.Gets.ShouldBe(1, "the advisory answer had room, so the attempt continues to the fetch");
        gate.Peeks.ShouldBe(1, "the deferred attempt is the one the pre-check asks about");
        gate.Checks.ShouldBe(1, "and the transactional gate still decides the batch once");
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(1m);
    }

    [Fact]
    public async Task A_PreCheck_With_Room_Still_Rolls_Back_When_The_Counter_Fills_Before_The_Transaction()
    {
        // The counter is filled by another connection right after the advisory answer, the way a sibling batch takes
        // the last unit of room between the pre-check and the write.
        Func<Task>? fill = null;
        var storage = new ReportStorageSpy();
        var gate = new ReportPreCheckGate(_ => fill?.Invoke() ?? Task.CompletedTask);
        var world = await StartAsync(services =>
        {
            ReplaceObjectStorage(services, inner => storage.Wrap(inner));
            ReplaceQuotaGate(services, inner => gate.Wrap(inner));
        });
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 0m, limit: 1m);
        await SeedDeferredTaskAsync(world, artifactId);
        fill = () => _fixture.ExecuteAsync(
            world.Database,
            $"update public.quota_counter set used = quota_limit where org_id = '{world.Org:D}'");

        var outcome = await ParseAsync(world, artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        outcome.Events.ShouldBe(0);
        gate.Peeks.ShouldBe(1);
        gate.Checks.ShouldBe(1, "the advisory answer admitted nothing, so the batch was still judged");
        storage.Gets.ShouldBe(1, "the pre-check let the attempt through, which is what makes the race reachable");

        // The block came from the transaction, so the evidence rolled back and the stamp it earned survives the
        // rollback in its own scope. The pre-check admitted this attempt, so it wrote no stamp of its own.
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountAsync(world, "event_batch")).ShouldBe(0);
        (await CountRawReportsAsync(world, artifactId)).ShouldBe(0);
        (await RunStateAsync(world)).ShouldBe("queued");
        var counter = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        counter.Used.ShouldBe(1m, "the concurrent fill is the only charge");
        counter.BlockedAt.ShouldNotBeNull("a rolled-back block stamps the counter, and this one was a block");
    }

    [Fact]
    public async Task A_First_Attempt_Is_Never_Pre_Checked()
    {
        var storage = new ReportStorageSpy();
        var gate = new ReportPreCheckGate();
        var world = await StartAsync(services =>
        {
            ReplaceObjectStorage(services, inner => storage.Wrap(inner));
            ReplaceQuotaGate(services, inner => gate.Wrap(inner));
        });
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 10m, limit: 10m);

        var outcome = await ParseAsync(world, artifactId);

        // The queue has not deferred this report, so the attempt is decided the ordinary way: fetched, parsed and
        // rolled back by the transactional gate. A pre-check here could refuse an attempt that still fits.
        outcome.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        gate.Peeks.ShouldBe(0, "only a deferred report is asked before its bytes are read");
        gate.Checks.ShouldBe(1);
        storage.Gets.ShouldBe(1);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().BlockedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_Gate_Is_Asked_About_The_Transactions_Month_Not_The_Application_Clock()
    {
        var spy = new QuotaGateSpy();
        // A month the database is never in: were the request built from the app clock, the seeded counter below
        // would not match and the batch would be admitted without charging anything.
        var elsewhere = new FakeTimeProvider(new DateTimeOffset(2020, 1, 15, 0, 0, 0, TimeSpan.Zero));
        var world = await StartAsync(services =>
        {
            UseClock(services, elsewhere);
            ReplaceQuotaGate(services, inner => new SpyQuotaGate(inner, spy));
        });
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 5m, limit: 10m);

        var outcome = await ParseAsync(world, artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        var request = spy.Requests.ShouldHaveSingleItem();
        request.Period.ShouldBeNull("the gate resolves the month inside its own first statement");
        var answer = spy.Answers.ShouldHaveSingleItem();
        answer.Period.ShouldBe(await PeriodAsync(world));
        answer.Period.ShouldNotBe(elsewhere.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_Raise_That_Leaves_Less_Than_One_Event_Re_Stamps_And_A_Repeat_Stays_Idempotent()
    {
        var gate = new ReportPreCheckGate();
        var world = await StartAsync(services => ReplaceQuotaGate(services, inner => gate.Wrap(inner)));
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 10m, limit: 10m);
        await SeedDeferredTaskAsync(world, artifactId);

        var first = await ParseAsync(world, artifactId);

        first.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        first.Reason.ShouldBe("quota_exceeded");
        var stamped = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        stamped.BlockedAt.ShouldNotBeNull("the pre-check's block stamps the counter the queue deferred");
        gate.Peeks.ShouldBe(1);

        // The raise lifts the block but leaves half a unit of room: one event still does not fit, so the next
        // pre-check must stamp the counter again instead of trusting the stamp the raise cleared.
        (await RaiseAsync(counters, world, world.Project, limit: 10.5m)).IsSuccess.ShouldBeTrue();
        var raised = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        raised.BlockedAt.ShouldBeNull("the raise is the one transition that clears the stamp");
        raised.Version.ShouldBeGreaterThan(stamped.Version);

        var again = await ParseAsync(world, artifactId);

        again.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        again.QuotaRequest.ShouldNotBeNull().Period.ShouldBe(await PeriodAsync(world));
        var reStamped = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        reStamped.BlockedAt.ShouldNotBeNull("too little room after a raise is still a refusal an operator must see");
        reStamped.Version.ShouldBeGreaterThan(raised.Version);
        (await SummaryBlockedAtAsync(counters, world)).ShouldNotBeNull("the summary reports the stamp the block wrote");

        var version = reStamped.Version;
        var stamp = reStamped.BlockedAt;
        var audit = await AuditCountAsync(world);

        (await ParseAsync(world, artifactId)).Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        (await ParseAsync(world, artifactId)).Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);

        var stable = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        stable.BlockedAt.ShouldBe(stamp, "a counter that already carries the stamp is left untouched");
        stable.Version.ShouldBe(version, "the stamp matches only unstamped counters, so a repeat writes no row");
        (await AuditCountAsync(world)).ShouldBe(audit, "the stamp writes no audit row");
        gate.Peeks.ShouldBe(4);
    }

    [Fact]
    public async Task A_Transient_Database_Fault_In_The_PreCheck_Defers_Without_An_Attempt_And_Stamps_Nothing()
    {
        // A connection reset is the shape a failover produces: the driver classifies it as transient, which is the one
        // kind of database fault the probe turns into a deferral.
        var gate = new ReportPreCheckGate(
            peekFault: () => new NpgsqlException("the quota store is unavailable", new IOException("connection reset")));
        var world = await StartAsync(services => ReplaceQuotaGate(services, inner => gate.Wrap(inner)));
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 10m, limit: 10m);
        await SeedDeferredTaskAsync(world, artifactId);
        var before = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        var log = new CapturedLog();

        var outcome = await ParseAsync(world, artifactId, log);

        outcome.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        outcome.Reason.ShouldBe("quota_precheck_unavailable", "a fault is not a refusal word");
        outcome.RetryAfter.ShouldBe(ParseRawReportHandler.PreCheckFaultDelay, "a fault waits long enough for a failover to finish");
        outcome.QuotaRequest.ShouldBeNull("nothing refused, so there is nothing to stamp");
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountRawReportsAsync(world, artifactId)).ShouldBe(0);

        // Only the exception's type and the SQLSTATE are logged: a database message can quote counter values and ids.
        log.Lines.ShouldContain(line => line.Contains("NpgsqlException", StringComparison.Ordinal));
        log.Lines.ShouldContain(line => line.Contains("sqlstate none", StringComparison.Ordinal));
        log.Lines.ShouldNotContain(line => line.Contains("the quota store is unavailable", StringComparison.Ordinal));

        var after = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        after.BlockedAt.ShouldBeNull();
        after.Version.ShouldBe(before.Version, "the fault path writes no counter row");

        // The task entry point answers the outcome with a deferral, never with the database exception.
        var deferred = await Should.ThrowAsync<TaskDeferredException>(
            async () => await CreateHandler(world).HandleAsync(
                new ParseRawReportPayload(world.RunId, artifactId, ProducerKind), world.Org, Token));
        deferred.Delay.ShouldBeInRange(
            TaskDeferredException.MinimumDelay,
            ParseRawReportHandler.PreCheckFaultDelay,
            "the jitter only ever shortens the fault's wait, never lengthens it past the month-end cap");
    }

    [Fact]
    public async Task A_Permanent_Database_Fault_In_The_PreCheck_Fails_The_Attempt_With_Its_SqlState()
    {
        // A denied privilege is permanent: deferring it would retry a broken deployment until the task dead-letters.
        var gate = new ReportPreCheckGate(peekFault: () => new PostgresException(
            "permission denied for table quota_counter", "ERROR", "ERROR", PostgresErrorCodes.InsufficientPrivilege));
        var world = await StartAsync(services => ReplaceQuotaGate(services, inner => gate.Wrap(inner)));
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 10m, limit: 10m);
        await SeedDeferredTaskAsync(world, artifactId);
        var before = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();

        var failure = await Should.ThrowAsync<PostgresException>(
            async () => await ParseAsync(world, artifactId));

        failure.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // The task entry point lets it escape as an ordinary failure, so the queue spends the attempt.
        await Should.ThrowAsync<PostgresException>(
            async () => await CreateHandler(world).HandleAsync(
                new ParseRawReportPayload(world.RunId, artifactId, ProducerKind), world.Org, Token));

        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().ShouldBe(before);
        (await CountRawReportsAsync(world, artifactId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_Cancelled_Attempt_Is_Never_Turned_Into_A_Quota_Deferral()
    {
        var gate = new ReportPreCheckGate();
        var world = await StartAsync(services => ReplaceQuotaGate(services, inner => gate.Wrap(inner)));
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 10m, limit: 10m);
        await SeedDeferredTaskAsync(world, artifactId);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // The token, not the quota, is what stopped the attempt: a deferral here would hide a shutdown or a lost lease.
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await CreateHandler(world).ExecuteAsync(
                new ParseRawReportPayload(world.RunId, artifactId, ProducerKind), world.Org, cancelled.Token));

        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().BlockedAt.ShouldBeNull();
        (await CountRawReportsAsync(world, artifactId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_Second_Refusing_Scopes_Stamp_Is_Written_Again_After_Only_That_Scope_Is_Raised()
    {
        var gate = new ReportPreCheckGate();
        var world = await StartAsync(services => ReplaceQuotaGate(services, inner => gate.Wrap(inner)));
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 7m, limit: 7m);
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), null, await PeriodAsync(world), "results", 7m, 7m, WarnRatio, Org: world.Org));
        await SeedDeferredTaskAsync(world, artifactId);

        // Both scopes refuse and both carry the stamp a raise has to clear.
        (await ParseAsync(world, artifactId)).Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().BlockedAt.ShouldNotBeNull();
        var tenant = (await ReadTenantCounterAsync(counters, world)).ShouldNotBeNull();
        tenant.BlockedAt.ShouldNotBeNull();

        // Only the project is raised, and not far enough: 7.999 leaves it refusing too, so the counter that is now
        // unstamped is the second scope the peek reports on, not the first one it returns.
        (await RaiseAsync(counters, world, world.Project, limit: 7.999m)).IsSuccess.ShouldBeTrue();
        var raised = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        raised.BlockedAt.ShouldBeNull();

        var again = await ParseAsync(world, artifactId);

        again.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        again.QuotaRequest.ShouldNotBeNull(
            "a still-refusing second scope without a stamp needs the write, even though the returned scope has one")
            .ProjectId.ShouldBe(world.Project);
        var reStamped = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        reStamped.BlockedAt.ShouldNotBeNull("the raise cleared the stamp, so the refusal must be visible again");
        reStamped.Version.ShouldBeGreaterThan(raised.Version);
        (await SummaryBlockedAtAsync(counters, world)).ShouldBe(reStamped.BlockedAt);
        (await ReadTenantCounterAsync(counters, world)).ShouldNotBeNull().ShouldBe(
            tenant, "the scope that already carries its stamp is not written again");
    }

    [Fact]
    public async Task A_Probe_Fault_On_A_First_Attempt_Cannot_Defer_It()
    {
        // No deferred task row yet: the pre-check is not reached at all, so the fault this gate would throw is never
        // asked and the attempt is parsed instead of being answered as a quota wait.
        var gate = new ReportPreCheckGate(peekFault: () => new TimeoutException("the quota read timed out"));
        var world = await StartAsync(services => ReplaceQuotaGate(services, inner => gate.Wrap(inner)));
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 0m, limit: 10m);
        var log = new CapturedLog();

        var outcome = await ParseAsync(world, artifactId, log);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        gate.Peeks.ShouldBe(0, "a first attempt is never questioned about quota");
        log.Lines.ShouldNotContain(line => line.Contains("deferred", StringComparison.OrdinalIgnoreCase));
        log.Lines.ShouldNotContain(line => line.Contains("quota_blocked_then_closed", StringComparison.Ordinal));
        (await CountRawReportsAsync(world, artifactId)).ShouldBe(1);
    }

    [Fact]
    public async Task An_Already_Stamped_Refusal_Opens_No_Stamp_Transaction_On_The_Next_Wait()
    {
        var gate = new ReportPreCheckGate();
        var world = await StartAsync(services => ReplaceQuotaGate(services, inner => gate.Wrap(inner)));
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 10m, limit: 10m);
        await SeedDeferredTaskAsync(world, artifactId);
        (await ParseAsync(world, artifactId)).Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        var stamped = (await ReadCounterAsync(counters, world)).ShouldNotBeNull();
        stamped.BlockedAt.ShouldNotBeNull();
        world.Host.Statements.Clear();

        var repeat = await ParseAsync(world, artifactId);

        repeat.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        repeat.QuotaRequest.ShouldBeNull("a counter that already carries the stamp needs no write");

        // The peek still reads the counter, but no statement writes one: neither the stamp nor the carry-forward runs.
        var counterStatements = world.Host.Statements.Snapshot()
            .Where(statement => statement.Contains("public.quota_counter", StringComparison.Ordinal))
            .ToList();
        counterStatements.ShouldNotBeEmpty("the pre-check still asks the counter");
        counterStatements.ShouldNotContain(
            statement => statement.Contains("update public.quota_counter", StringComparison.Ordinal)
                || statement.Contains("insert into public.quota_counter", StringComparison.Ordinal),
            "a repeated refusal writes no counter row, so its stamp transaction never opens");
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().ShouldBe(stamped);
    }

    [Fact]
    public async Task A_Non_Database_Fault_In_The_PreCheck_Still_Propagates()
    {
        var gate = new ReportPreCheckGate(peekFault: () => new InvalidOperationException("the pre-check is broken"));
        var world = await StartAsync(services => ReplaceQuotaGate(services, inner => gate.Wrap(inner)));
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 10m, limit: 10m);
        await SeedDeferredTaskAsync(world, artifactId);

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            async () => await ParseAsync(world, artifactId));

        failure.Message.ShouldBe("the pre-check is broken", "only a database fault is turned into a deferral");
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountRawReportsAsync(world, artifactId)).ShouldBe(0);
    }

    [Fact]
    public async Task The_PreCheck_Asks_About_The_Runs_Project_And_Another_Scopes_Full_Counter_Never_Blocks()
    {
        var gate = new ReportPreCheckGate();
        var world = await StartAsync(services => ReplaceQuotaGate(services, inner => gate.Wrap(inner)));
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);

        // The artifact names another project of the org. Nothing ties the row's project to its run's, so the pre-check
        // must ask the run's project — the counter the write transaction charges — and not the artifact's.
        var otherProject = await SeedOtherProjectAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody, otherProject);
        var (foreignOrg, foreignProject) = await SeedForeignOrgAsync(world);
        await SeedCounterAsync(counters, world, used: 0m, limit: 10m);
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), otherProject, await PeriodAsync(world), "results", 5m, 5m, WarnRatio, Org: world.Org));
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), foreignProject, await PeriodAsync(world), "results", 5m, 5m, WarnRatio, Org: foreignOrg));
        await SeedDeferredTaskAsync(world, artifactId);
        var otherBefore = (await counters.CounterAsync(world.Org, otherProject, await PeriodAsync(world))).ShouldNotBeNull();
        var foreignBefore = (await counters.CounterAsync(foreignOrg, foreignProject, await PeriodAsync(world))).ShouldNotBeNull();

        var outcome = await ParseAsync(world, artifactId);

        // Both other counters are full: had the pre-check asked either of them it would have deferred here.
        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);
        gate.PeekRequests.ShouldHaveSingleItem().ProjectId.ShouldBe(
            world.Project, "the run's project is the counter the write transaction charges");
        gate.Checks.ShouldBe(1);
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(1m);

        var otherAfter = (await counters.CounterAsync(world.Org, otherProject, await PeriodAsync(world))).ShouldNotBeNull();
        otherAfter.Used.ShouldBe(otherBefore.Used);
        otherAfter.Version.ShouldBe(otherBefore.Version);
        var foreignAfter = (await counters.CounterAsync(foreignOrg, foreignProject, await PeriodAsync(world))).ShouldNotBeNull();
        foreignAfter.Used.ShouldBe(foreignBefore.Used);
        foreignAfter.Version.ShouldBe(foreignBefore.Version, "a counter of another tenant is never charged or stamped");
    }

    /// <summary>
    /// The production relation a deferred report leaves behind: one outbox row keyed by the artifact, and the task the
    /// dispatcher makes from it, whose key is <c>'outbox:' || outbox.id</c> and whose last error is the deferral's text.
    /// </summary>
    /// <param name="world">The world the report belongs to.</param>
    /// <param name="artifactId">The report artifact the deferred task parses.</param>
    /// <param name="queue">The queue the task row names, so a case that polls a worker claims only its own rows.</param>
    /// <returns>The seeded task row's id.</returns>
    private async Task<Guid> SeedDeferredTaskAsync(World world, Guid artifactId, string queue = "tasks")
    {
        var outboxId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var payload = $$"""
            {"run_id":"{{world.RunId:D}}","artifact_id":"{{artifactId:D}}","producer_kind":"{{ProducerKind}}"}
            """;
        // The deferral's text carries an apostrophe, so it is escaped for the literal.
        var persistedError = TaskDeferredException.PersistedError.Replace("'", "''", StringComparison.Ordinal);

        await _fixture.ExecuteAsync(world.Database, $$"""
            insert into public.outbox (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key)
            values ('{{outboxId:D}}', '{{world.Org:D}}', 'artifact', '{{artifactId:D}}', '{{EvidenceTaskTypes.ReportParse}}',
                '{{payload}}'::jsonb, 'evidence:artifact:{{artifactId:D}}:{{EvidenceTaskTypes.ReportParse}}');

            insert into public.task (id, org_id, queue, task_type, payload, idempotency_key, state, last_error)
            values ('{{taskId:D}}', '{{world.Org:D}}', '{{queue}}', '{{EvidenceTaskTypes.ReportParse}}',
                '{{payload}}'::jsonb, 'outbox:{{outboxId:D}}', 'retry', '{{persistedError}}')
            """);

        return taskId;
    }

    /// <summary>Swaps the host's object store for a wrapper that counts the reads the handler performs.</summary>
    private static void ReplaceObjectStorage(IServiceCollection services, Func<IObjectStorage, IObjectStorage> wrap)
    {
        var registration = services.Last(descriptor => descriptor.ServiceType == typeof(IObjectStorage));
        var implementation = registration.ImplementationType
            ?? throw new InvalidOperationException("the host's IObjectStorage registration must name its implementation");

        services.RemoveAll<IObjectStorage>();
        services.AddSingleton<IObjectStorage>(provider =>
            wrap((IObjectStorage)ActivatorUtilities.CreateInstance(provider, implementation)));
    }
}

/// <summary>The object store the attempt reads from, counted: one read per attempt that reaches the fetch.</summary>
internal sealed class ReportStorageSpy : IObjectStorage, IDisposable
{
    private int _gets;
    private IObjectStorage? _inner;

    /// <summary>How many whole-object reads the handler made.</summary>
    internal int Gets => Volatile.Read(ref _gets);

    /// <summary>Counts reads from <paramref name="inner"/> and answers every other call through it.</summary>
    /// <param name="inner">The store the host registered.</param>
    /// <returns>This spy, so a test can register it in one expression.</returns>
    internal IObjectStorage Wrap(IObjectStorage inner)
    {
        _inner = inner;

        return this;
    }

    private IObjectStorage Inner =>
        _inner ?? throw new InvalidOperationException("the spy was not wrapped around the host's store");

    /// <inheritdoc />
    public PresignedPut PresignPut(PresignPutRequest request) => Inner.PresignPut(request);

    /// <inheritdoc />
    public PresignedGet PresignGet(string objectKey) => Inner.PresignGet(objectKey);

    /// <inheritdoc />
    public Task<ObjectHead?> HeadAsync(string objectKey, CancellationToken cancellationToken) =>
        Inner.HeadAsync(objectKey, cancellationToken);

    /// <inheritdoc />
    public Task<byte[]?> ReadPrefixAsync(
        string objectKey,
        int maxBytes,
        CancellationToken cancellationToken,
        string? ifMatch = null) =>
        Inner.ReadPrefixAsync(objectKey, maxBytes, cancellationToken, ifMatch);

    /// <inheritdoc />
    public Task<byte[]?> GetAsync(string objectKey, long maxBytes, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _gets);

        return Inner.GetAsync(objectKey, maxBytes, cancellationToken);
    }

    /// <inheritdoc />
    public Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken) =>
        Inner.CopyAsync(sourceKey, destinationKey, cancellationToken);

    /// <inheritdoc />
    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) =>
        Inner.DeleteAsync(objectKey, cancellationToken);

    /// <summary>The host's store is this wrapper's to close, because DI now owns the wrapper instead.</summary>
    public void Dispose() => (_inner as IDisposable)?.Dispose();
}

/// <summary>
/// The registered gate, counted by method: the pre-check's advisory question and the transaction's authority are told
/// apart, <paramref name="afterPeek"/> lets a case change the counter between the two, and <paramref name="peekFault"/>
/// makes the advisory read fail the way a database fault does.
/// </summary>
/// <param name="afterPeek">Runs after the advisory answer and before the authoritative one, or null for none.</param>
/// <param name="peekFault">Builds the exception the advisory read throws instead of answering, or null for none.</param>
internal sealed class ReportPreCheckGate(
    Func<CancellationToken, Task>? afterPeek = null,
    Func<Exception>? peekFault = null) : IQuotaGate
{
    private int _peeks;
    private int _checks;
    private readonly ConcurrentQueue<QuotaCheckRequest> _peekRequests = new();
    private IQuotaGate? _inner;

    /// <summary>How many advisory questions the handler asked.</summary>
    internal int Peeks => Volatile.Read(ref _peeks);

    /// <summary>How many authoritative checks the handler asked.</summary>
    internal int Checks => Volatile.Read(ref _checks);

    /// <summary>Every advisory question, in arrival order.</summary>
    internal IReadOnlyList<QuotaCheckRequest> PeekRequests => [.. _peekRequests];

    private IQuotaGate Inner =>
        _inner ?? throw new InvalidOperationException("the gate was not wrapped around the host's own");

    /// <summary>Wraps the host's own gate, so the counters above are the ones the handler feeds.</summary>
    /// <param name="inner">The gate the host registered.</param>
    /// <returns>This gate, so the caller keeps the counters it asserts on.</returns>
    internal IQuotaGate Wrap(IQuotaGate inner)
    {
        _inner = inner;

        return this;
    }

    /// <inheritdoc />
    public Task<QuotaCheckResult> CheckAsync(QuotaCheckRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _checks);

        return Inner.CheckAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<QuotaPeekResult> PeekAsync(QuotaCheckRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _peeks);
        _peekRequests.Enqueue(request);

        if (peekFault is not null)
        {
            throw peekFault();
        }

        var answer = await Inner.PeekAsync(request, cancellationToken).ConfigureAwait(false);

        if (afterPeek is not null)
        {
            await afterPeek(cancellationToken).ConfigureAwait(false);
        }

        return answer;
    }
}
