using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportQuotaTests
{
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
}
