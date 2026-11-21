using System.Diagnostics.Metrics;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// The decision-latency, run-creation and authoring instruments, kept apart from the pipeline and ingest
/// ones so each file stays readable and under the size limit.
/// </summary>
public static partial class EvidgateTelemetry
{
    /// <summary>The decision-latency histogram's name (the frozen→decided latency the reliability SLI reads).</summary>
    public const string EvaluationDecisionLatencyMetricName = "evidgate_evaluation_decision_latency_seconds";

    /// <summary>Counter of project AI settings saves, tagged by outcome.</summary>
    public const string AiSettingsWriteMetricName = "evidgate_authoring_ai_settings_write_total";

    private static readonly Counter<long> AiSettingsWrites = EvidgateMeter.Instance.CreateCounter<long>(
        AiSettingsWriteMetricName,
        unit: "{save}",
        description: "Project AI settings saves by outcome (created, noop, stale, invalid). No labels beyond the closed outcome vocabulary: never a settings value, a URL or a hash.");

    /// <summary>
    /// Counts one project AI settings save, tagged by the closed outcome vocabulary: <c>created</c> (a new
    /// version appended), <c>noop</c> (identical content), <c>stale</c> (the precondition lost) or
    /// <c>invalid</c> (the request was refused).
    /// </summary>
    /// <param name="outcome">One of <c>created</c>, <c>noop</c>, <c>stale</c> or <c>invalid</c>.</param>
    public static void RecordAiSettingsWrite(string outcome) =>
        AiSettingsWrites.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Counter of runs created, tagged by trigger event, fork flag and unverified-ref flag.</summary>
    public const string RunsCreatedMetricName = "evidgate_runs_created_total";

    private static readonly Counter<long> RunsCreated = EvidgateMeter.Instance.CreateCounter<long>(
        RunsCreatedMetricName,
        unit: "{run}",
        description: "Runs created, by trigger_event (closed vocabulary or none), is_fork and unverified_ref. Never a ref name, a sha or an id, so the series count stays bounded.");

    /// <summary>
    /// Counts one committed run creation. A fork run is advisory-only, so its share is the signal that fork traffic
    /// reaches the API at all.
    /// </summary>
    /// <param name="triggerEvent">The validated trigger event, or null when the run recorded none.</param>
    /// <param name="isFork">Whether the run's identity says fork.</param>
    /// <param name="unverifiedRef">Whether any identity value came from the request body.</param>
    public static void RecordRunCreated(string? triggerEvent, bool isFork, bool unverifiedRef) =>
        RunsCreated.Add(
            1,
            new KeyValuePair<string, object?>("trigger_event", triggerEvent ?? "none"),
            new KeyValuePair<string, object?>("is_fork", isFork),
            new KeyValuePair<string, object?>("unverified_ref", unverifiedRef));

    /// <summary>Counter of queued authoring changes, tagged by the closed change-type vocabulary.</summary>
    public const string AuthoringChangeQueuedMetricName = "evidgate_authoring_change_queued_total";

    private static readonly Counter<long> AuthoringChangesQueued = EvidgateMeter.Instance.CreateCounter<long>(
        AuthoringChangeQueuedMetricName,
        unit: "{change}",
        description: "Authoring changes queued, by change type (comment, delete, merge, split). Never a title, a comment body or an id: only the closed type vocabulary, so the tag stays low-cardinality.");

    /// <summary>
    /// Counts one successfully queued authoring change. A refused queue is not counted: the request's own status and
    /// the error code already describe it, and this counter answers "how much review work is arriving".
    /// </summary>
    /// <param name="changeType">One of <c>comment</c>, <c>delete</c>, <c>merge</c> or <c>split</c>.</param>
    public static void RecordAuthoringChangeQueued(string changeType) =>
        AuthoringChangesQueued.Add(1, new KeyValuePair<string, object?>("change_type", changeType));

    /// <summary>Counter of batched applies, tagged by the closed outcome vocabulary.</summary>
    public const string AuthoringChangeApplyMetricName = "evidgate_authoring_apply_total";

    private static readonly Counter<long> AuthoringChangesApplied = EvidgateMeter.Instance.CreateCounter<long>(
        AuthoringChangeApplyMetricName,
        unit: "{apply}",
        description: "Batched authoring applies by outcome (applied, stale, conflict). Never an ids list or a count: only the closed vocabulary, so the tag stays low-cardinality.");

    /// <summary>
    /// Counts one apply attempt, tagged by the closed outcome vocabulary: <c>applied</c> (the batch committed),
    /// <c>stale</c> (a precondition or a version lost) or <c>conflict</c> (a selected change was no longer pending).
    /// A refused request is counted here: "how often do applies lose their precondition" is the number the plan asks
    /// for, and the selection's own size is the separate batch-size histogram.
    /// </summary>
    /// <param name="outcome">One of <c>applied</c>, <c>stale</c> or <c>conflict</c>.</param>
    public static void RecordAuthoringApply(string outcome) =>
        AuthoringChangesApplied.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Histogram of how many changes one committed apply carried.</summary>
    public const string AuthoringChangeApplyBatchSizeMetricName = "evidgate_authoring_apply_batch_size";

    /// <remarks>
    /// The instrument counts in <see cref="long"/>, like every other count in this class: integral counts are the
    /// unit the exporters and dashboards read, and every reader of the platform's meters subscribes to int64
    /// measurements. A <see cref="MeterListener"/> is the sharpest case — it receives exactly the value types whose
    /// callbacks it registered, so a listener that watches <c>long</c> and <c>double</c> (the two an exporter binds)
    /// never sees a measurement of an instrument typed <see cref="int"/>. Keeping the instrument int64 and letting
    /// the call site pass an <see cref="int"/> costs nothing, because the conversion is implicit.
    /// </remarks>
    private static readonly Histogram<long> AuthoringChangeApplyBatchSizes = EvidgateMeter.Instance.CreateHistogram<long>(
        AuthoringChangeApplyBatchSizeMetricName,
        unit: "{change}",
        description: "Changes carried by one committed apply. No labels: batches are compared as a distribution, which answers \"do maintainers batch\" without storing a selection.",
        advice: new InstrumentAdvice<long>
        {
            HistogramBucketBoundaries = [1, 2, 3, 5, 8, 13, 20, 50, 100, 200],
        });

    /// <summary>Records the selection size of one committed apply.</summary>
    /// <param name="changeCount">The number of changes the batch applied; 1..200 in practice.</param>
    public static void RecordAuthoringApplyBatchSize(int changeCount) =>
        AuthoringChangeApplyBatchSizes.Record(changeCount);

    private static readonly Histogram<double> EvaluationDecisionLatencies = EvidgateMeter.Instance.CreateHistogram<double>(
        EvaluationDecisionLatencyMetricName,
        unit: "s",
        description: "Seconds between a frozen evidence revision and the evaluation row that decided it. No labels: a run id or a decision token would be unbounded, and the SLI is read as a distribution.",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60],
        });

    /// <summary>
    /// Records how long a run's evidence waited between its freeze and its stored
    /// decision. Called once per newly committed evaluation, from the scope's
    /// <see cref="Persistence.Tenancy.ITenantScope.AfterCommit"/>: a conflicted
    /// duplicate and a rolled-back attempt record nothing, so the histogram counts
    /// decisions, not attempts.
    /// </summary>
    /// <param name="frozenToDecided">The row's <c>created_at</c> minus the revision's freeze; zero or more.</param>
    public static void RecordEvaluationDecisionLatency(TimeSpan frozenToDecided)
    {
        // A negative span would mean the database stamped the decision before the evidence it read, so it is clamped
        // rather than recorded: one clock-skew artefact must not drag the SLI.
        var seconds = Math.Max(0, frozenToDecided.TotalSeconds);
        EvaluationDecisionLatencies.Record(seconds);
    }
}
