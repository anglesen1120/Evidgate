using System.Data.Common;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportParseTests
{
    [Fact]
    public async Task RawReport_OverlappingSuccessfulParses_CommitExactlyOneWholeEventSet()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifact = await SeedArtifactAsync(world, MappedBody);
        var result = await SuccessfulChildAsync(world);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task RendezvousAsync()
        {
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                bothEntered.TrySetResult();
            }

            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        }

        var firstRunner = new StubParseRunner { Result = result, DuringRun = RendezvousAsync };
        var replayRunner = new StubParseRunner { Result = result, DuringRun = RendezvousAsync };
        var outcomes = await Task.WhenAll(
            ParseAsync(world, artifact, ProducerKind, runner: firstRunner),
            ParseAsync(world, artifact, ProducerKind, runner: replayRunner));

        arrivals.ShouldBe(2);
        outcomes.ShouldAllBe(outcome => outcome.Status == ParseRawReportOutcome.Parsed);
        outcomes.Sum(outcome => outcome.Events).ShouldBe(1);
        (await RawReportCountAsync(world, artifact)).ShouldBe(1);
        Number(await RawReportAsync(world, artifact), "observed_tests").ShouldBe(1);
        (await CountAsync(world, "event", artifact)).ShouldBe(1);
        (await CountAsync(world, "case_attempt")).ShouldBe(1);
        (await CountAsync(world, "producer_slot")).ShouldBe(1);
        (await CountAsync(world, "outbox")).ShouldBe(1);
    }

    [Fact]
    public async Task RawReport_CrashWinsWhileSuccessfulParseIsRunning_RefusedRowHasNoCommittedEvents()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifact = await SeedArtifactAsync(world, MappedBody);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var successful = new StubParseRunner
        {
            Result = await SuccessfulChildAsync(world),
            DuringRun = async () =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
            },
        };
        var first = ParseAsync(world, artifact, ProducerKind, runner: successful);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
            var crashed = new StubParseRunner
            {
                Result = new ParseRunFailed(ParseRunFailureKind.ExitedAbnormally, 3, 0, 0),
            };
            var refusal = await ParseAsync(world, artifact, ProducerKind, runner: crashed);
            await AssertRefusedRowAsync(world, artifact, refusal, ParseRunRejection.Crashed);
        }
        finally
        {
            release.TrySetResult();
        }

        await first;
        var stored = await RawReportAsync(world, artifact);
        Text(stored, "outcome").ShouldBe("malformed");
        Number(stored, "observed_tests").ShouldBe(0);
        (await CountAsync(world, "event", artifact)).ShouldBe(0,
            "an immutable malformed row must not coexist with events committed by its overlapping delivery");
        await AssertNothingWrittenAsync(world, artifact);
    }

    [Fact]
    public async Task RawReport_CrashAfterSuccessfulParse_DoesNotRewriteTheCommittedReport()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifact = await SeedArtifactAsync(world, MappedBody);
        (await ParseAsync(world, artifact, ProducerKind)).Events.ShouldBe(1);
        var crashed = new StubParseRunner
        {
            Result = new ParseRunFailed(ParseRunFailureKind.ExitedAbnormally, 3, 0, 0),
        };

        await ParseAsync(world, artifact, ProducerKind, runner: crashed);

        (await RawReportCountAsync(world, artifact)).ShouldBe(1);
        var stored = await RawReportAsync(world, artifact);
        Text(stored, "outcome").ShouldBe("mapped");
        Text(stored, "reason").ShouldBeNull();
        Number(stored, "observed_tests").ShouldBe(1);
        (await CountAsync(world, "event", artifact)).ShouldBe(1);
    }

    [Fact]
    public async Task RawReport_UnsupportedKindReplay_NeverStartsAChildAndStoresOnlyOneRefusal()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifact = await SeedArtifactAsync(world, MappedBody);
        var runner = new StubParseRunner
        {
            Throws = new InvalidOperationException("unsupported producer must not reach the child"),
        };

        var outcomes = await Task.WhenAll(
            ParseAsync(world, artifact, UnknownKind, runner: runner),
            ParseAsync(world, artifact, UnknownKind, runner: runner));

        outcomes.ShouldAllBe(outcome => outcome.Status == ParseRawReportOutcome.Unsupported);
        runner.Calls.ShouldBe(0);
        (await RawReportCountAsync(world, artifact)).ShouldBe(1);
        var row = await RawReportAsync(world, artifact);
        Text(row, "reason").ShouldBe("unknown_producer_kind");
        Text(row, "parser_id").ShouldBeNull();
        Text(row, "parser_version").ShouldBeNull();
        Discrepancies(row).ShouldBeEmpty();
        await AssertNothingWrittenAsync(world, artifact);

        var supported = await SeedArtifactAsync(world, MappedBody);
        (await ParseAsync(world, supported, ProducerKind)).Events.ShouldBe(1);
    }

    [Fact]
    public async Task RawReport_FailureAfterItsInsert_RollsBackTheReportAndEventsAndReplayRecovers()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifact = await SeedArtifactAsync(world, MappedBody);
        var db = new FailAfterReportInsertDb(world.Host.Services.GetRequiredService<ITenantDb>());

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => ParseWithDbAsync(world, artifact, db));

        error.Message.ShouldBe("failure after raw_report insert");
        db.ReportRowsBeforeFailure.ShouldBe(1);
        db.EventRowsBeforeFailure.ShouldBe(1);
        (await RawReportCountAsync(world, artifact)).ShouldBe(0);
        await AssertNothingWrittenAsync(world, artifact);

        (await ParseAsync(world, artifact, ProducerKind)).Events.ShouldBe(1);
        (await RawReportCountAsync(world, artifact)).ShouldBe(1);
        (await CountAsync(world, "event", artifact)).ShouldBe(1);
    }

    private static async Task<ParseRunResult> SuccessfulChildAsync(World world) =>
        await new EmulatedChildRunner(world.Host.Services.GetRequiredService<IParsing>()).RunAsync(
            new ParseRunRequest(ParseRunnerProfile.Xml, 1 << 20, XmlType, new MemoryStream(MappedBody), 1000),
            Token);

    private sealed class FailAfterReportInsertDb(ITenantDb inner) : ITenantDb
    {
        public long ReportRowsBeforeFailure { get; private set; }

        public long EventRowsBeforeFailure { get; private set; }

        public DbConnection Connection => inner.Connection;

        public DbTransaction Transaction => inner.Transaction;

        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            inner.QueryAsync<T>(sql, parameters, cancellationToken);

        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            inner.QuerySingleOrDefaultAsync<T>(sql, parameters, cancellationToken);

        public Task<T?> ScalarAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            inner.ScalarAsync<T>(sql, parameters, cancellationToken);

        public async Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            var affected = await inner.ExecuteAsync(sql, parameters, cancellationToken);
            if (sql.Contains("insert into public.raw_report", StringComparison.Ordinal))
            {
                ReportRowsBeforeFailure = await inner.ScalarAsync<long>(
                    "select count(*) from public.raw_report where artifact_id = @artifact_id", parameters, cancellationToken);
                EventRowsBeforeFailure = await inner.ScalarAsync<long>(
                    "select count(*) from public.event where artifact_ids = array[@artifact_id::uuid]", parameters, cancellationToken);
                throw new InvalidOperationException("failure after raw_report insert");
            }

            return affected;
        }
    }
}
