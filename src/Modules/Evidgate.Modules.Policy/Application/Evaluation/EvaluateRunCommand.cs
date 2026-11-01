using System.Globalization;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Evaluation;
using Evidgate.Modules.Policy.Infrastructure.Persistence;

namespace Evidgate.Modules.Policy.Application.Evaluation;

/// <summary>
/// Evaluates one run's frozen evidence against its pinned policy revision and stores the verdict the decision read and
/// the merge gate consume. Not an HTTP command — no route and no user permission reaches it; the worker's
/// <c>orchestrator.evidence.revision_frozen</c> task dispatches it.
/// </summary>
/// <param name="RunId">The run whose evidence is judged.</param>
/// <param name="EvidenceRevision">The frozen <c>evidence_revision.revision_no</c> the task names.</param>
/// <param name="Mode">The slot the verdict goes into: the single live one, or a shadow opinion.</param>
internal sealed record EvaluateRunCommand(Guid RunId, int EvidenceRevision, EvaluationMode Mode);

/// <summary>One stored evaluation, as the command returns it (the insert's own row or the winner it lost to).</summary>
/// <param name="Id">The <c>evaluation.id</c>.</param>
/// <param name="RunId">The run the verdict is about.</param>
/// <param name="EvidenceRevision">The frozen revision it judged.</param>
/// <param name="PolicyRevisionId">The policy revision the run was pinned to.</param>
/// <param name="PolicyHash">That revision's content address.</param>
/// <param name="ManifestHash">The manifest the evidence froze against.</param>
/// <param name="Completeness">The evaluator's <c>complete</c>/<c>incomplete</c> token.</param>
/// <param name="Missing">The closed <c>missing[]</c> codes behind an incomplete verdict.</param>
/// <param name="Outcome">What the required scope's results say.</param>
/// <param name="Decision">The final decision token.</param>
/// <param name="Reasons">The closed <c>reasons[]</c> codes, sorted by token.</param>
/// <param name="EvaluatorVersion">The evaluator that produced it.</param>
/// <param name="Mode">The stored <c>live</c>/<c>shadow</c> slot.</param>
/// <param name="ExceptionsHash">The content address of the exception snapshot it was decided under.</param>
/// <param name="ExceptionsAsOf">The instant that snapshot was read.</param>
/// <param name="CreatedAt">When the row committed.</param>
internal sealed record EvaluationRecord(
    Guid Id,
    Guid RunId,
    int EvidenceRevision,
    Guid PolicyRevisionId,
    string PolicyHash,
    string ManifestHash,
    string Completeness,
    IReadOnlyList<string> Missing,
    string Outcome,
    string Decision,
    IReadOnlyList<string> Reasons,
    string EvaluatorVersion,
    string Mode,
    string ExceptionsHash,
    DateTimeOffset ExceptionsAsOf,
    DateTimeOffset CreatedAt);

/// <summary>
/// Handler of <see cref="EvaluateRunCommand"/>: loads the inputs, calls the pure evaluator
/// (<see cref="IPolicyEvaluator"/>), then lets the database decide who wrote the row. The insert is arbitrated by the
/// table's two unique keys with <c>do nothing</c> — the six-column idempotency key and the partial live key — so the
/// loser of a race stores nothing and reads the winner back by the six columns, which is what makes a redelivered task
/// and a concurrent attempt produce one verdict instead of two; a live row it lost under other rules raises there
/// rather than being returned as this attempt's answer.
/// </summary>
/// <remarks>
/// Every failure here raises instead of returning a failure <see cref="Result{T}"/>: the only caller is the worker's
/// task handler, where an exception is what rolls the attempt back and schedules the retry, and a returned failure would
/// be read as success. A missing run, revision or policy revision cannot be repaired by retrying, so it reaches the
/// dead-letter list with the reason in the message.
/// </remarks>
internal sealed class EvaluateRunCommandHandler(
    EvaluationInputReader inputs,
    IPolicyEvaluator evaluator,
    ITenantDb db,
    ITenantScopeAccessor scopes,
    TimeProvider time)
{
    /// <summary>
    /// The canonical JSON of "nothing was waived or quarantined": an empty array. Resolving the real active set, and
    /// the instant it was read, belongs to the exceptions-snapshot task that depends on the waiver and quarantine
    /// tables; until then the handler stores this explicit empty snapshot rather than a value that could be mistaken
    /// for resolved data.
    /// </summary>
    private const string EmptyExceptions = "[]";

    /// <summary>The row the insert itself returned, when the key was free.</summary>
    private sealed record InsertedRow(Guid Id, DateTimeOffset CreatedAt);

    /// <summary>The winner's row, when this attempt lost the insert.</summary>
    private sealed record StoredRow(
        Guid Id,
        Guid RunId,
        int EvidenceRevision,
        Guid PolicyRevisionId,
        string PolicyHash,
        string ManifestHash,
        string Completeness,
        string[] Missing,
        string Outcome,
        string Decision,
        string[] Reasons,
        string EvaluatorVersion,
        string Mode,
        string ExceptionsHash,
        DateTimeOffset ExceptionsAsOf,
        DateTimeOffset CreatedAt);

    /// <summary>Evaluates and stores, or returns the row that already holds this key.</summary>
    public async ValueTask<Result<EvaluationRecord>> Handle(
        EvaluateRunCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.RunId == Guid.Empty || command.EvidenceRevision < 1 || !Enum.IsDefined(command.Mode))
        {
            throw new InvalidOperationException(
                $"the evaluation task names no run or no frozen revision (run {command.RunId:D}, "
                + $"revision {command.EvidenceRevision.ToString(CultureInfo.InvariantCulture)}, mode {command.Mode})");
        }

        var loaded = await inputs.LoadAsync(command.RunId, command.EvidenceRevision, cancellationToken)
            .ConfigureAwait(false);

        var mode = EvaluationModeCode.Of(command.Mode);
        var orgId = scopes.Require().OrgId;

        // Captured once per attempt, before the evaluator runs and never re-read: the row says which instant the
        // exception set was read at, so two attempts of one task cannot disagree about it.
        var exceptionsAsOf = time.GetUtcNow();
        var exceptionsHash = CanonicalHash.Compute(EmptyExceptions);

        var result = evaluator.Evaluate(
            loaded.Evidence,
            loaded.Policy,
            new ExceptionsSnapshotInput(exceptionsAsOf, [], []));

        var inserted = await db.QuerySingleOrDefaultAsync<InsertedRow>(
            EvaluationSql.Insert,
            new
            {
                id = Guid.CreateVersion7(),
                org_id = orgId,
                run_id = command.RunId,
                evidence_revision = command.EvidenceRevision,
                policy_revision_id = loaded.PolicyRevisionId,
                policy_hash = loaded.PolicyHash,
                manifest_hash = loaded.ManifestHash,
                completeness = EvaluationCodes.Code(result.Completeness),
                missing = string.Join(EvaluationSql.ArraySeparator, result.Missing.Select(EvaluationCodes.Code)),
                outcome = EvaluationCodes.Code(result.Outcome),
                decision = EvaluationCodes.Code(result.Decision),
                reasons = string.Join(EvaluationSql.ArraySeparator, result.Reasons.Select(EvaluationCodes.Code)),
                evaluator_version = IPolicyEvaluator.EvaluatorVersion,
                mode,
                exceptions_hash = exceptionsHash,
                exceptions_as_of = exceptionsAsOf,
                array_separator = EvaluationSql.ArraySeparator,
            },
            cancellationToken).ConfigureAwait(false);

        if (inserted is not null)
        {
            // The measurement describes a durable verdict, so it waits for the commit this handler does not own: a
            // rolled-back attempt and the conflict-loser path record nothing, and a repeat task records nothing new.
            scopes.Require().AfterCommit(() =>
                EvidgateTelemetry.RecordEvaluationDecisionLatency(inserted.CreatedAt - loaded.FrozenAt));

            return Result.Success(new EvaluationRecord(
                inserted.Id,
                command.RunId,
                command.EvidenceRevision,
                loaded.PolicyRevisionId,
                loaded.PolicyHash,
                loaded.ManifestHash,
                EvaluationCodes.Code(result.Completeness),
                result.Missing.Select(EvaluationCodes.Code).ToArray(),
                EvaluationCodes.Code(result.Outcome),
                EvaluationCodes.Code(result.Decision),
                result.Reasons.Select(EvaluationCodes.Code).ToArray(),
                IPolicyEvaluator.EvaluatorVersion,
                mode,
                exceptionsHash,
                exceptionsAsOf,
                inserted.CreatedAt));
        }

        var stored = await db.QuerySingleOrDefaultAsync<StoredRow>(
            EvaluationSql.SelectExisting,
            new
            {
                org_id = orgId,
                run_id = command.RunId,
                evidence_revision = command.EvidenceRevision,
                policy_revision_id = loaded.PolicyRevisionId,
                evaluator_version = IPolicyEvaluator.EvaluatorVersion,
                mode,
            },
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "the conflicting evaluation row could not be read back, so this attempt returns no verdict");

        return Result.Success(new EvaluationRecord(
            stored.Id,
            stored.RunId,
            stored.EvidenceRevision,
            stored.PolicyRevisionId,
            stored.PolicyHash,
            stored.ManifestHash,
            stored.Completeness,
            stored.Missing,
            stored.Outcome,
            stored.Decision,
            stored.Reasons,
            stored.EvaluatorVersion,
            stored.Mode,
            stored.ExceptionsHash,
            stored.ExceptionsAsOf,
            stored.CreatedAt));
    }
}
