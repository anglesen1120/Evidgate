using System.Diagnostics;
using System.Diagnostics.Metrics;
using Evidgate.BuildingBlocks.Observability.Correlation;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// Hosts subscribe to this <see cref="ActivitySource"/> and <see cref="Meter"/> so both entry points
/// use one telemetry vocabulary. The <c>evidgate.module</c> tag identifies the modules a trace crosses.
/// </summary>
public static class EvidgateTelemetry
{
    /// <summary>Name of the single application activity source (00 §5.7, plan 10).</summary>
    public const string ActivitySourceName = "Evidgate";

    /// <summary>Name of the single application meter.</summary>
    public const string MeterName = "Evidgate";

    /// <summary>Span/metric tag naming the module a unit of work belongs to (00 §5.1 names).</summary>
    public const string ModuleTag = "evidgate.module";

    /// <summary>Span tag naming the module operation, e.g. <c>FinalizeShard</c>.</summary>
    public const string OperationTag = "evidgate.operation";

    /// <summary>Span tag carrying the 00 §9.1 request id.</summary>
    public const string RequestIdTag = "evidgate.request_id";

    /// <summary>Span tag carrying the producer id a batch's events declare.</summary>
    public const string ProducerIdTag = "evidgate.producer_id";

    /// <summary>Span tag carrying how many events the parsed batch holds.</summary>
    public const string EventCountTag = "evidgate.event_count";

    /// <summary>Counter of module operations, tagged by <see cref="ModuleTag"/>.</summary>
    public const string ModuleOperationCounterName = "evidgate.module.operations";

    /// <summary>Counter of manifests refused at upload, tagged by reason (05.1.1 §11).</summary>
    public const string ManifestRejectedMetricName = "evidgate_manifest_rejected_total";

    /// <summary>Counter of adapter capabilities refused at first send, tagged by code and protocol-major bucket (04.7.1-T2).</summary>
    public const string AdapterCapabilityRejectedMetricName = "evidgate_adapter_capability_rejected_total";

    /// <summary>Counter of event batches refused by a transport or decode cap, tagged by reason.</summary>
    public const string IngestBatchRejectedMetricName = "evidgate_ingest_batch_rejected_total";

    /// <summary>
    /// Counter of ingestion requests refused by one published cap, tagged by the cap name only
    /// (<c>manifest_bytes</c>, <c>manifest_executions</c>, <c>manifest_variants</c>, <c>report_cases</c>,
    /// <c>report_bytes</c>, <c>run_body_bytes</c>). Never labelled with a value, an id or a body.
    /// </summary>
    public const string IngestCapExceededMetricName = "evidgate_ingest_cap_exceeded_total";

    /// <summary>Counter of case attempts written inside a caller's tenant transaction, tagged by normalised status (05.3.3-T2.1).</summary>
    public const string CaseAttemptWrittenMetricName = "evidgate_case_attempt_written_total";

    /// <summary>Counter of dependant case attempts marked <c>blocked</c> because their setup failed (05.3.3-T2.2).</summary>
    public const string CaseAttemptBlockedBySetupMetricName = "evidgate_case_attempt_blocked_by_setup_total";

    /// <summary>Counter of finalize requests refused because the producer slot is bound to another identity (05.3.2-T1, the T9 abuse signal).</summary>
    public const string FinalizeProducerSlotTakenMetricName = "evidgate_finalize_producer_slot_taken_total";

    /// <summary>Counter of repeat finalizes whose declared values disagree with the stored declaration (05.3.2-T1, the T68/D3 signal).</summary>
    public const string FinalizeDeclarationMismatchMetricName = "evidgate_finalize_declaration_mismatch_total";

    /// <summary>Counter of release components a bind could not match, added per applied bind. No labels: never a key or a digest.</summary>
    public const string ReleaseBindMismatchMetricName = "evidgate_release_bind_mismatch_total";

    /// <summary>Histogram of pipeline-handler durations in milliseconds (01.4.2-T4.1, the C2 telemetry stage).</summary>
    public const string PipelineDurationMetricName = "evidgate.pipeline.duration";

    /// <summary>
    /// Counter of federated login attempts on the <c>start</c>/<c>callback</c> routes, tagged by provider and
    /// outcome code (02.3.1-T1.1). The vocabulary is closed: provider ∈ the registered names (or
    /// <c>unknown</c>), outcome ∈ <c>started</c>, <c>completed</c> and the 00 §9.2 codes the routes answer
    /// (<c>token_invalid</c>, <c>unavailable</c>, <c>not_found</c>, <c>no_membership</c>,
    /// <c>account_disabled</c>). Never
    /// labelled with an email, a subject, a state, a nonce, a verifier or an authorization code.
    /// </summary>
    public const string LoginAttemptMetricName = "evidgate_login_attempts_total";

    /// <summary>
    /// Counter of cookie-authenticated requests by session-auth outcome (02.3.2-T1.1, §8): <c>active</c>, or one
    /// of the closed 00 §9.2 codes the request path answers (<c>unauthenticated</c>, <c>session_expired</c>,
    /// <c>session_revoked</c>, <c>account_disabled</c>, <c>csrf_failed</c>). Never labelled with a cookie value,
    /// a session id, an address or a user agent (D9).
    /// </summary>
    public const string SessionAuthOutcomeMetricName = "evidgate_session_auth_total";

    /// <summary>Counter of session rotations, tagged by trigger (login, privilege change). Never labelled with an id.</summary>
    public const string SessionRotatedMetricName = "evidgate_session_rotated_total";

    /// <summary>Counter of revoked sessions by count: one add per revoke-all call, labelled with nothing (D9).</summary>
    public const string SessionRevokedMetricName = "evidgate_session_revoked_total";

    /// <summary>
    /// Counter of session-path writes, tagged <c>written=true|false</c>: the idle-slide throttle's hit rate
    /// (02.3.2 §8). A request inside the 60 s window counts once with <c>written=false</c>.
    /// </summary>
    public const string SessionSlideMetricName = "evidgate_session_slide_total";

    /// <summary>Span tag naming the message type a pipeline span ran (e.g. <c>FinalizeShard</c>).</summary>
    public const string PipelineMessageTag = "evidgate.message";

    /// <summary>Span tag carrying the thrown exception's type on a failed pipeline run (OTel <c>error.type</c>).</summary>
    public const string PipelineErrorTag = "error.type";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> ModuleOperations = Meter.CreateCounter<long>(
        ModuleOperationCounterName,
        unit: "{operation}",
        description: "Module operations started, tagged by evidgate.module.");

    private static readonly Counter<long> ManifestRejections = Meter.CreateCounter<long>(
        ManifestRejectedMetricName,
        unit: "{manifest}",
        description: "Manifests refused at upload, by reason (05.1.1). Never labelled with a manifest value, case name or hash.");

    private static readonly Counter<long> AdapterCapabilityRejections = Meter.CreateCounter<long>(
        AdapterCapabilityRejectedMetricName,
        unit: "{capability}",
        description: "Adapter capabilities refused at first send, by code and protocol-major bucket (04.7.1). Never labelled with a document value, adapter name or hash.");

    private static readonly Counter<long> IngestBatchRejections = Meter.CreateCounter<long>(
        IngestBatchRejectedMetricName,
        unit: "{batch}",
        description: "Event batches refused by a transport or decode cap, by reason (04.1.1). Never labelled with a body, an event id or any request value.");

    private static readonly Counter<long> IngestCapExceeded = Meter.CreateCounter<long>(
        IngestCapExceededMetricName,
        unit: "{request}",
        description: "Ingestion requests refused by one published cap, by cap name. Never labelled with a value, an id or a body.");

    private static readonly Counter<long> CaseAttemptsWritten = Meter.CreateCounter<long>(
        CaseAttemptWrittenMetricName,
        unit: "{attempt}",
        description: "Case attempts written inside a caller's tenant transaction, by normalised status (05.3.3). Never labelled with a raw status, case name or execution key.");

    private static readonly Counter<long> CaseAttemptsBlockedBySetup = Meter.CreateCounter<long>(
        CaseAttemptBlockedBySetupMetricName,
        unit: "{attempt}",
        description: "Dependant case attempts marked blocked because their setup failed (05.3.3-T2.2). No labels.");

    private static readonly Counter<long> FinalizeProducerSlotTaken = Meter.CreateCounter<long>(
        FinalizeProducerSlotTakenMetricName,
        unit: "{request}",
        description: "Finalize requests refused with producer_slot_taken (05.3.2-T1, T9). No labels: never a token, a producer id or a run id.");

    private static readonly Counter<long> FinalizeDeclarationMismatch = Meter.CreateCounter<long>(
        FinalizeDeclarationMismatchMetricName,
        unit: "{request}",
        description: "Repeat finalizes whose declared values disagree with the stored declaration (05.3.2-T1, T68/D3). No labels.");

    private static readonly Counter<long> ReleaseBindMismatch = Meter.CreateCounter<long>(
        ReleaseBindMismatchMetricName,
        unit: "{component}",
        description: "Release components a bind could not match. No labels: never a component key, a digest or an id.");

    private static readonly Histogram<double> PipelineDurations = Meter.CreateHistogram<double>(
        PipelineDurationMetricName,
        unit: "ms",
        description: "C2 pipeline handler duration by message type.");

    private static readonly Counter<long> LoginAttempts = Meter.CreateCounter<long>(
        LoginAttemptMetricName,
        unit: "{attempt}",
        description: "Federated login attempts by provider and outcome code (02.3.1). Never labelled with an email, a subject, a state, a nonce, a verifier or a code.");

    private static readonly Counter<long> SessionAuthOutcomes = Meter.CreateCounter<long>(
        SessionAuthOutcomeMetricName,
        unit: "{request}",
        description: "Cookie-authenticated requests by session-auth outcome code (02.3.2). Never labelled with a cookie value, a session id, an address or a user agent.");

    private static readonly Counter<long> SessionRotations = Meter.CreateCounter<long>(
        SessionRotatedMetricName,
        unit: "{rotation}",
        description: "Session rotations by trigger (02.3.2). No ids: a rotation is counted, never attributed.");

    private static readonly Counter<long> SessionRevocations = Meter.CreateCounter<long>(
        SessionRevokedMetricName,
        unit: "{session}",
        description: "Sessions revoked, added in the batch each revoke-all call revoked (02.3.2). No labels.");

    private static readonly Counter<long> SessionSlides = Meter.CreateCounter<long>(
        SessionSlideMetricName,
        unit: "{request}",
        description: "Session-path writes by whether the idle slide was actually written (02.3.2's 60 s throttle hit rate). No labels beyond written.");

    /// <summary>
    /// Starts the span for one module operation, tagged with the module name
    /// (AC6). Returns null when nothing listens, so callers use
    /// <c>using var activity = …</c> unconditionally.
    /// </summary>
    public static Activity? StartModuleActivity(string module, string operation)
    {
        // The ambient activity is read before the new span starts: a child inherits the
        // parent's baggage but none of its tags (10.3.1-T1.2, AC1).
        var parent = Activity.Current;
        var activity = Source.StartActivity(operation, ActivityKind.Internal);
        activity?.SetTag(ModuleTag, module);
        activity?.SetTag(OperationTag, operation);
        EvidgateCorrelationExtensions.ApplyAmbient(activity, parent);

        ModuleOperations.Add(1, new KeyValuePair<string, object?>(ModuleTag, module));

        return activity;
    }

    /// <summary>
    /// Starts the span for one pipeline message (01.4.2-T4.1): tagged with the
    /// message type but NOT with <see cref="ModuleTag"/> — the pipeline is not a
    /// module, so the per-module tag vocabulary stays accurate. Returns null when
    /// nothing listens.
    /// </summary>
    public static Activity? StartPipelineActivity(string message)
    {
        var parent = Activity.Current;
        var activity = Source.StartActivity(message, ActivityKind.Internal);
        activity?.SetTag(PipelineMessageTag, message);
        EvidgateCorrelationExtensions.ApplyAmbient(activity, parent);

        return activity;
    }

    /// <summary>Counts one manifest refused for <paramref name="reason"/> (a closed vocabulary: <c>schema</c>, <c>unknown_case_revision</c>, …).</summary>
    /// <param name="reason">The reason token; never a value from the manifest.</param>
    public static void RecordManifestRejected(string reason) =>
        ManifestRejections.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Span tag carrying the cap that refused an ingestion request, e.g. <c>manifest_bytes</c>.</summary>
    public const string IngestCapTag = "ingest.cap";

    /// <summary>Counts one ingestion request refused by <paramref name="cap"/> and tags its trace, so a refusal shows in both.</summary>
    /// <param name="cap">One of the closed cap names; never a value from the request.</param>
    public static void RecordIngestCapExceeded(string cap)
    {
        // A name outside the closed set collapses to "other" so a refusal path never throws and the label stays bounded.
        cap = IsCapName(cap) ? cap : "other";

        IngestCapExceeded.Add(1, new KeyValuePair<string, object?>("cap", cap));
        Activity.Current?.SetTag(IngestCapTag, cap);
    }

    private static bool IsCapName(string cap) =>
        cap is "manifest_bytes" or "manifest_executions" or "manifest_variants" or "report_cases" or "report_bytes" or "run_body_bytes";

    /// <summary>
    /// Counts one adapter capability refused at first send (04.7.1-T2), tagged by the
    /// rejection <paramref name="code"/> and a bounded protocol-major bucket.
    /// </summary>
    /// <param name="code">The <c>ApiErrorCode</c> token: <c>capability_invalid</c>, <c>unsupported_protocol_major</c> or <c>capability_conflict</c>; never a document value.</param>
    /// <param name="protocolMajor">The adapter's declared major, bucketed to <c>1</c>–<c>8</c> or <c>other</c>; <c>null</c> when the document carried none.</param>
    public static void RecordAdapterCapabilityRejected(string code, int? protocolMajor) =>
        AdapterCapabilityRejections.Add(
            1,
            new KeyValuePair<string, object?>("code", code),
            new KeyValuePair<string, object?>("protocol_major", ProtocolMajorBucket(protocolMajor)));

    /// <summary>
    /// Counts one event batch refused before it was written, tagged by <paramref name="reason"/> from
    /// the closed vocabulary only — never a body, an event id or any request value.
    /// </summary>
    /// <param name="reason">One of <c>body_too_large</c>, <c>event_count</c>, <c>expanded_too_large</c>, <c>ratio_exceeded</c>, <c>encoding_unsupported</c>, <c>gzip_invalid</c>, <c>rate_limited</c>.</param>
    public static void RecordIngestBatchRejected(string reason) =>
        IngestBatchRejections.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>
    /// Counts <paramref name="count"/> case attempts written inside the caller's tenant transaction, by
    /// <paramref name="normalizedStatus"/> (05.3.3-T2.1 step 6). The label is the closed normalised vocabulary only —
    /// never a raw status, case name or execution key — and the count is a write count: a later rollback of the
    /// caller's transaction is not subtracted.
    /// </summary>
    /// <param name="normalizedStatus">One of the six normalised statuses; never a raw producer value.</param>
    /// <param name="count">The rows written; zero or less is a no-op.</param>
    public static void RecordCaseAttemptWritten(string normalizedStatus, long count)
    {
        if (count <= 0)
        {
            return;
        }

        CaseAttemptsWritten.Add(count, new KeyValuePair<string, object?>("normalized_status", normalizedStatus));
    }

    /// <summary>
    /// Counts <paramref name="count"/> dependant attempts marked <c>blocked</c> because their setup failed
    /// (05.3.3-T2.2 step d). No labels.
    /// </summary>
    /// <param name="count">The rows written; zero or less is a no-op.</param>
    public static void RecordCaseAttemptBlockedBySetup(long count)
    {
        if (count <= 0)
        {
            return;
        }

        CaseAttemptsBlockedBySetup.Add(count);
    }

    /// <summary>
    /// Counts one finalize request refused because the producer slot is bound to another identity (05.3.2-T1, the T9
    /// abuse signal the plan §11 names). No labels: the request's token, run id and producer id are never recorded.
    /// </summary>
    public static void RecordFinalizeProducerSlotTaken() => FinalizeProducerSlotTaken.Add(1);

    /// <summary>
    /// Counts the release components a bind could not match (<c>digest_mismatch</c> or <c>component_missing</c>), added
    /// once per applied bind. No labels: the component keys and digests themselves are never recorded.
    /// </summary>
    /// <param name="count">The mismatched components; zero or less is a no-op.</param>
    public static void RecordReleaseBindMismatch(long count)
    {
        if (count <= 0)
        {
            return;
        }

        ReleaseBindMismatch.Add(count);
    }

    /// <summary>
    /// Counts one repeat finalize whose declared values disagree with the values the first close stored (05.3.2-T1;
    /// the stored declaration stays authoritative — D3/Q1). No labels; the values themselves are never recorded.
    /// </summary>
    public static void RecordFinalizeDeclarationMismatch() => FinalizeDeclarationMismatch.Add(1);

    /// <summary>
    /// Counts one federated login attempt (02.3.1-T1.1) under the closed vocabulary of
    /// <see cref="LoginAttemptMetricName"/>: the provider's registered name (or <c>unknown</c> for a route
    /// value nothing answers to) and an outcome code — never a value from the request or the provider.
    /// </summary>
    /// <param name="provider">A registered provider name, or <c>unknown</c>.</param>
    /// <param name="outcome">One of <c>started</c>, <c>completed</c>, or the 00 §9.2 code the route answered.</param>
    public static void RecordLoginAttempt(string provider, string outcome) =>
        LoginAttempts.Add(
            1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>
    /// Counts one cookie-authenticated request by its session-auth <paramref name="outcome"/> (02.3.2 §8): the
    /// closed vocabulary of <see cref="SessionAuthOutcomeMetricName"/> only — never a cookie value, a session
    /// id, an address or a user agent (D9).
    /// </summary>
    /// <param name="outcome">One of <c>active</c>, <c>unauthenticated</c>, <c>session_expired</c>, <c>session_revoked</c>, <c>account_disabled</c>, <c>csrf_failed</c>.</param>
    public static void RecordSessionAuthOutcome(string outcome) =>
        SessionAuthOutcomes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Counts one session rotation (02.3.2 AC7), tagged with its trigger — never with an id.</summary>
    /// <param name="trigger">One of <c>login</c> (the callback rotated a forwarded cookie) or <c>privilege_change</c>.</param>
    public static void RecordSessionRotated(string trigger) =>
        SessionRotations.Add(1, new KeyValuePair<string, object?>("trigger", trigger));

    /// <summary>Counts <paramref name="count"/> revoked sessions (02.3.2 AC4); zero or less is a no-op. No labels.</summary>
    /// <param name="count">The rows revoked by one call.</param>
    public static void RecordSessionRevoked(long count)
    {
        if (count <= 0)
        {
            return;
        }

        SessionRevocations.Add(count);
    }

    /// <summary>
    /// Counts one session-path write decision (02.3.2 §8's slide throttle): <c>written=true</c> when the idle
    /// deadline moved, <c>false</c> when the request was inside the 60 s window. No labels beyond that flag.
    /// </summary>
    /// <param name="written">Whether the statement actually wrote the row.</param>
    public static void RecordSessionSlideWrite(bool written) =>
        SessionSlides.Add(1, new KeyValuePair<string, object?>("written", written));

    /// <summary>Bounds the protocol-major label to <c>1</c>–<c>8</c> (the majors a v1 server can see) or <c>other</c>, so the tag stays low-cardinality.</summary>
    private static string ProtocolMajorBucket(int? protocolMajor) => protocolMajor switch
    {
        1 => "1",
        2 => "2",
        3 => "3",
        4 => "4",
        5 => "5",
        6 => "6",
        7 => "7",
        8 => "8",
        _ => "other",
    };

    /// <summary>
    /// Records the handler duration of one pipeline message, tagged with the
    /// message type and whether it failed, so 10's dashboards split the two.
    /// </summary>
    public static void RecordPipelineDuration(string message, TimeSpan duration, bool error)
    {
        PipelineDurations.Record(
            duration.TotalMilliseconds,
            new KeyValuePair<string, object?>(PipelineMessageTag, message),
            new KeyValuePair<string, object?>("error", error));
    }

    /// <summary>The decision-latency histogram's name (the frozen→decided latency the reliability SLI reads).</summary>
    public const string EvaluationDecisionLatencyMetricName = "evidgate_evaluation_decision_latency_seconds";

    /// <summary>Counter of project AI settings saves, tagged by outcome.</summary>
    public const string AiSettingsWriteMetricName = "evidgate_authoring_ai_settings_write_total";

    private static readonly Counter<long> AiSettingsWrites = Meter.CreateCounter<long>(
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

    /// <summary>Counter of queued authoring changes, tagged by the closed change-type vocabulary.</summary>
    public const string AuthoringChangeQueuedMetricName = "evidgate_authoring_change_queued_total";

    private static readonly Counter<long> AuthoringChangesQueued = Meter.CreateCounter<long>(
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

    private static readonly Counter<long> AuthoringChangesApplied = Meter.CreateCounter<long>(
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
    private static readonly Histogram<long> AuthoringChangeApplyBatchSizes = Meter.CreateHistogram<long>(
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

    private static readonly Histogram<double> EvaluationDecisionLatencies = Meter.CreateHistogram<double>(
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
