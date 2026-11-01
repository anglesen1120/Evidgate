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

    /// <summary>
    /// One evaluation row: the values the insert sends, and the same shape the winner read-back returns, so one mapper
    /// answers both paths.
    /// </summary>
    private sealed record Row(
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

    /// <summary>The id and commit stamp the insert returned, when the key was free.</summary>
    private sealed record InsertedRow(Guid Id, DateTimeOffset CreatedAt);

    /// <summary>Evaluates and stores, or returns the row that already holds this key.</summary>
    public async ValueTask<Result<EvaluationRecord>> Handle(
        EvaluateRunCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);

        var loaded = await inputs.LoadAsync(command.RunId, command.EvidenceRevision, cancellationToken)
            .ConfigureAwait(false);

        var row = Decide(command, loaded);
        var inserted = await InsertAsync(row, cancellationToken).ConfigureAwait(false);

        if (inserted is null)
        {
            var winner = await ReadWinnerAsync(row, cancellationToken).ConfigureAwait(false);
            return Result.Success(ToRecord(winner));
        }

        // The measurement describes a durable verdict, so it waits for the commit this handler does not own: a
        // rolled-back attempt and the conflict-loser path record nothing, and a repeat task records nothing new.
        scopes.Require().AfterCommit(() =>
            EvidgateTelemetry.RecordEvaluationDecisionLatency(inserted.CreatedAt - loaded.FrozenAt));

        return Result.Success(ToRecord(row with { CreatedAt = inserted.CreatedAt }));
    }

    /// <summary>Refuses a command that names no run, no frozen revision or a mode the table would refuse.</summary>
    private static void Validate(EvaluateRunCommand command)
    {
        if (command.RunId == Guid.Empty || command.EvidenceRevision < 1 || !Enum.IsDefined(command.Mode))
        {
            throw new InvalidOperationException(
                $"the evaluation task names no run or no frozen revision (run {command.RunId:D}, "
                + $"revision {command.EvidenceRevision.ToString(CultureInfo.InvariantCulture)}, mode {command.Mode})");
        }
    }

    /// <summary>The row this attempt would store, computed once from the durable inputs and the captured snapshot.</summary>
    private Row Decide(EvaluateRunCommand command, LoadedEvaluationInputs loaded)
    {
        // Captured once per attempt, before the evaluator runs and never re-read: the row says which instant the
        // exception set was read at, so two attempts of one task cannot disagree about it.
        var exceptionsAsOf = time.GetUtcNow();
        var exceptionsHash = CanonicalHash.Compute(EmptyExceptions);

        var result = evaluator.Evaluate(
            loaded.Evidence,
            loaded.Policy,
            new ExceptionsSnapshotInput(exceptionsAsOf, [], []));

        return new Row(
            Guid.CreateVersion7(),
            command.RunId,
            command.EvidenceRevision,
            loaded.PolicyRevisionId,
            loaded.PolicyHash,
            loaded.ManifestHash,
            EvaluationCodes.Code(result.Completeness),
            [.. result.Missing.Select(EvaluationCodes.Code)],
            EvaluationCodes.Code(result.Outcome),
            EvaluationCodes.Code(result.Decision),
            [.. result.Reasons.Select(EvaluationCodes.Code)],
            IPolicyEvaluator.EvaluatorVersion,
            EvaluationModeCode.Of(command.Mode),
            exceptionsHash,
            exceptionsAsOf,
            default);
    }

    /// <summary>Inserts the row; null means a unique key already holds the answer to this question.</summary>
    private async Task<InsertedRow?> InsertAsync(Row row, CancellationToken cancellationToken) =>
        await db.QuerySingleOrDefaultAsync<InsertedRow>(
            EvaluationSql.Insert,
            new
            {
                id = row.Id,
                org_id = scopes.Require().OrgId,
                run_id = row.RunId,
                evidence_revision = row.EvidenceRevision,
                policy_revision_id = row.PolicyRevisionId,
                policy_hash = row.PolicyHash,
                manifest_hash = row.ManifestHash,
                completeness = row.Completeness,
                missing = string.Join(EvaluationSql.ArraySeparator, row.Missing),
                outcome = row.Outcome,
                decision = row.Decision,
                reasons = string.Join(EvaluationSql.ArraySeparator, row.Reasons),
                evaluator_version = row.EvaluatorVersion,
                mode = row.Mode,
                exceptions_hash = row.ExceptionsHash,
                exceptions_as_of = row.ExceptionsAsOf,
                array_separator = EvaluationSql.ArraySeparator,
            },
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The row the losing insert must answer with. A live verdict already stored under another policy revision or
    /// evaluator version is not this attempt's row, so the empty read raises instead of inventing one.
    /// </summary>
    private async Task<Row> ReadWinnerAsync(Row row, CancellationToken cancellationToken) =>
        await db.QuerySingleOrDefaultAsync<Row>(
            EvaluationSql.SelectExisting,
            new
            {
                org_id = scopes.Require().OrgId,
                run_id = row.RunId,
                evidence_revision = row.EvidenceRevision,
                policy_revision_id = row.PolicyRevisionId,
                evaluator_version = row.EvaluatorVersion,
                mode = row.Mode,
            },
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the conflicting evaluation row could not be read back, so this attempt returns no verdict: a live "
                + $"evaluation of run {row.RunId:D} revision "
                + $"{row.EvidenceRevision.ToString(CultureInfo.InvariantCulture)} already exists under a different "
                + "policy revision or evaluator version");

    /// <summary>The one mapper: the insert's own row and the winner read back become the same record.</summary>
    private static EvaluationRecord ToRecord(Row row) =>
        new(
            row.Id,
            row.RunId,
            row.EvidenceRevision,
            row.PolicyRevisionId,
            row.PolicyHash,
            row.ManifestHash,
            row.Completeness,
            row.Missing,
            row.Outcome,
            row.Decision,
            row.Reasons,
            row.EvaluatorVersion,
            row.Mode,
            row.ExceptionsHash,
            row.ExceptionsAsOf,
            row.CreatedAt);
}
