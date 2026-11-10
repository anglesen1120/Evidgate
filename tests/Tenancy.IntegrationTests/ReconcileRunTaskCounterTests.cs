using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The run-incomplete counter measures a decision the platform committed: it is registered on the attempt's tenant
/// scope, so only the consumer's commit counts it, and only a settlement that decides the run registers it at all.
/// The reconciler and the finisher are replaced, so the counter's placement is the subject and no database is involved;
/// the suite's exclusive collection keeps a sibling attempt from moving the process-wide counter inside an assertion.
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
public sealed class ReconcileRunTaskCounterTests
{
    private static readonly Guid RunId = Guid.CreateVersion7();

    /// <summary>
    /// A forced close that settles the run <c>incomplete</c> is one committed decision: the attempt registers the
    /// measurement, the commit runs it, and the counter grows once — never while the transaction is still open.
    /// </summary>
    [Fact]
    public async Task IncompleteSettlement_Is_Counted_Once_On_The_Commit()
    {
        var scope = new TestTenantScope();
        var handler = Handler(
            new ReconcileOutcome(ReconcileDisposition.ComparedOverdue, Comparison()),
            IncompleteFinalization(revisionNo: 1));

        using var listener = IngestTelemetryCapture.IncompleteRuns(out var measurements);
        var before = measurements.Count;

        await handler.HandleAsync(Payload(), scope, Token);

        measurements.Count.ShouldBe(before, "the measurement waits for the commit the consumer owns");
        scope.RegisteredCount.ShouldBe(1, "one settlement, one measurement registered for the commit");

        await scope.CommitAsync(Token);

        scope.ExecutedCount.ShouldBe(1, "the commit is what runs the measurement");
        measurements.Count.ShouldBe(before + 1, "the committed decision is counted once");
    }

    /// <summary>
    /// A rolled-back attempt records nothing: the incomplete verdict it settled never became durable, so the counter
    /// must not grow. The registration proves the attempt meant to measure; the missing commit is what cancels it.
    /// </summary>
    [Fact]
    public async Task IncompleteSettlement_RolledBack_Is_Not_Counted()
    {
        var scope = new TestTenantScope();
        var handler = Handler(
            new ReconcileOutcome(ReconcileDisposition.Compared, Comparison()),
            IncompleteFinalization(revisionNo: 1));

        using var listener = IngestTelemetryCapture.IncompleteRuns(out var measurements);
        var before = measurements.Count;

        await handler.HandleAsync(Payload(), scope, Token);

        // The attempt ends without a commit, which is the rollback a crashed consumer performs.
        await scope.DisposeAsync();

        scope.RegisteredCount.ShouldBe(1, "the settled decision registered its measurement");
        scope.ExecutedCount.ShouldBe(0, "a scope disposed without a commit runs no post-commit work");
        measurements.Count.ShouldBe(before, "a rolled-back settlement is not a counted decision");
    }

    /// <summary>
    /// A late or reprocess attempt appends a revision and settles no state: even when the finisher answers with the
    /// revision the run's evidence already has and that revision is <c>incomplete</c>, the attempt is not a decision
    /// the rate describes, so it registers nothing at all.
    /// </summary>
    [Fact]
    public async Task LateAppend_Of_An_Incomplete_Revision_Is_Not_Counted()
    {
        var scope = new TestTenantScope();
        var finisher = IncompleteFinalization(revisionNo: 2);
        var handler = Handler(
            new ReconcileOutcome(ReconcileDisposition.ComparedLate, Comparison()),
            finisher);

        using var listener = IngestTelemetryCapture.IncompleteRuns(out var measurements);
        var before = measurements.Count;

        await handler.HandleAsync(Payload(), scope, Token);
        await scope.CommitAsync(Token);

        finisher.Settlements.ShouldHaveSingleItem().ShouldBe(
            ReconcileSettlement.AppendLateEvidence, "the late path appends, it never decides");
        scope.RegisteredCount.ShouldBe(0, "an append settles no state, so there is no decision to count");
        measurements.Count.ShouldBe(before);
    }

    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    /// <summary>The production handler over stubs, so the attempt reaches the counter without a database or a queue.</summary>
    private static ReconcileRunTaskHandler Handler(ReconcileOutcome outcome, StubFinisher finisher) =>
        new(Services(outcome, finisher).GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ReconcileRunTaskHandler>.Instance);

    /// <summary>A settled comparison with every shard closed and nothing missing.</summary>
    private static ReconcileComparison Comparison() => new([], ShardsPlanned: 1, ShardsClosed: 1, EventWatermark: 0);

    /// <summary>The finisher's answer for a run settled <c>incomplete</c>.</summary>
    private static StubFinisher IncompleteFinalization(int revisionNo) => new(
        new ReconcileFinalization(ReconcileFinisher.Incomplete, Guid.CreateVersion7(), revisionNo));

    /// <summary>One finalize trigger, the ordinary path a settled run's last shard enqueues.</summary>
    private static RunReconcilePayload Payload() => new(RunId, ProducerId: "shard-1", DeclaredLastSequence: 1, DeclaredCount: 1);

    private static ServiceProvider Services(ReconcileOutcome outcome, StubFinisher finisher)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRunReconciler>(new StubReconciler(outcome));
        services.AddSingleton<IReconcileFinisher>(finisher);

        return services.BuildServiceProvider();
    }

    private sealed class StubReconciler(ReconcileOutcome outcome) : IRunReconciler
    {
        public ValueTask<ReconcileOutcome> ReconcileAsync(
            Guid runId,
            bool lateTrigger,
            bool reprocessTrigger,
            CancellationToken cancellationToken) => ValueTask.FromResult(outcome);
    }

    private sealed class StubFinisher(ReconcileFinalization finalization) : IReconcileFinisher
    {
        private readonly List<ReconcileSettlement> _settlements = [];

        internal IReadOnlyList<ReconcileSettlement> Settlements
        {
            get
            {
                lock (_settlements)
                {
                    return [.. _settlements];
                }
            }
        }

        public ValueTask<ReconcileFinalization> FinishAsync(
            Guid runId,
            ReconcileComparison comparison,
            ReconcileSettlement settlement,
            CancellationToken cancellationToken)
        {
            lock (_settlements)
            {
                _settlements.Add(settlement);
            }

            return ValueTask.FromResult(finalization);
        }
    }
}
