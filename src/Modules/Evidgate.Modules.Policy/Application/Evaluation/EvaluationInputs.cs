using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Evaluation;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Policy.Infrastructure.Persistence;

namespace Evidgate.Modules.Policy.Application.Evaluation;

/// <summary>Where a verdict is stored: the single <c>live</c> slot of an evidence revision, or advice beside it.</summary>
internal enum EvaluationMode
{
    /// <summary>The verdict of record; the partial unique index admits one per evidence revision.</summary>
    Live,

    /// <summary>A second opinion about the same evidence: stored beside the live row, never instead of it.</summary>
    Shadow,
}

/// <summary>The stored spellings of <see cref="EvaluationMode"/> (the column's CHECK is the same closed list).</summary>
internal static class EvaluationModeCode
{
    /// <summary>The <c>evaluation.mode</c> token.</summary>
    internal static string Of(EvaluationMode mode) => mode switch
    {
        EvaluationMode.Live => "live",
        EvaluationMode.Shadow => "shadow",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "not an evaluation mode"),
    };
}

/// <summary>
/// Everything the evaluator reads, assembled from durable rows instead of the caller: the frozen revision's own codes
/// and its results, the run's pinned policy revision and the plan the evidence is judged against.
/// </summary>
/// <param name="Evidence">The frozen evidence revision and the results it froze over.</param>
/// <param name="Policy">The pinned policy revision's decision configuration.</param>
/// <param name="PolicyRevisionId">The revision the row stores, so the verdict names the rules it applied.</param>
/// <param name="PolicyHash">The revision's content address, stored on the row.</param>
/// <param name="ManifestHash">The manifest the evidence revision froze against, stored on the row.</param>
/// <param name="FrozenAt">The revision's <c>created_at</c>: the start of the decision latency.</param>
/// <param name="ProjectId">The run's project, whose waivers and quarantine entries the snapshot reads.</param>
/// <param name="IsReplay">Whether the run is a replay, which forces the shadow slot.</param>
internal sealed record LoadedEvaluationInputs(
    EvidenceRevisionInput Evidence,
    PolicyRevisionInput Policy,
    Guid PolicyRevisionId,
    string PolicyHash,
    string ManifestHash,
    DateTimeOffset FrozenAt,
    Guid ProjectId,
    bool IsReplay);

/// <summary>
/// Builds <see cref="LoadedEvaluationInputs"/> from the run's own rows. The run, its plan, the pinned suite, the frozen
/// revision, its attempts and the case behind each planned revision come from the modules that own those tables
/// (<see cref="IOrchestratorEvaluationPlan"/>, <see cref="ICatalogContracts"/>, <see cref="IFrozenEvidence"/>), each on
/// the caller's ambient tenant scope; only the pinned policy revision is this module's own read. Nothing is taken from
/// the task payload but the ids it names.
/// </summary>
/// <remarks>
/// A run that pins no policy revision, a revision number that does not exist, a pinned policy revision that does not
/// exist and a policy body this reader cannot parse all raise: the evaluator would otherwise judge evidence against
/// rules nobody pinned, and a retry cannot make a missing row appear, so the exception is what an operator sees in the
/// dead-letter list.
/// </remarks>
internal sealed class EvaluationInputReader(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IOrchestratorEvaluationPlan plans,
    ICatalogContracts catalog,
    IFrozenEvidence frozenEvidence)
{
    /// <summary>The completeness token a revision must carry for its <c>missing[]</c> to speak for itself.</summary>
    private const string CompleteCompleteness = "complete";

    /// <summary>The <c>run.purpose</c> of a replay, whose verdict may only ever be advice.</summary>
    private const string ReplayPurpose = "replay";

    /// <summary>A policy revision's id, content address and body.</summary>
    private sealed record PolicyRow(Guid Id, string Hash, string Body);

    /// <summary>One planned execution's latest attempt, plus the manifest's own required flag.</summary>
    private sealed record ResultRow(Guid CaseId, string CaseKey, string Status, bool Required);

    /// <summary>Loads the inputs for one attempt.</summary>
    /// <param name="runId">The run the evaluation belongs to.</param>
    /// <param name="evidenceRevision">The frozen <c>revision_no</c> the task names.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal async Task<LoadedEvaluationInputs> LoadAsync(
        Guid runId,
        int evidenceRevision,
        CancellationToken cancellationToken)
    {
        var orgId = scopes.Require().OrgId;

        var plan = await RequirePlanAsync(orgId, runId, cancellationToken).ConfigureAwait(false);

        var policyRevisionId = plan.PolicyRevisionId
            ?? throw new InvalidOperationException(
                "run pins no policy revision, so there are no rules to judge its evidence against");

        var suite = await RequireSuiteAsync(orgId, plan.SuiteRevisionId, cancellationToken).ConfigureAwait(false);
        var frozen = await RequireFrozenAsync(orgId, runId, evidenceRevision, cancellationToken)
            .ConfigureAwait(false);
        var revision = frozen.Revision;
        var policy = await RequirePolicyAsync(orgId, policyRevisionId, cancellationToken).ConfigureAwait(false);
        var results = await ResultsAsync(orgId, frozen.Attempts, plan.Executions, cancellationToken)
            .ConfigureAwait(false);

        var evidence = new EvidenceRevisionInput(
            plan.PlannedCount,
            suite.DeclaredEmpty,
            MissingCodesOf(revision),
            results
                .Select(row => new CaseResultInput(
                    row.CaseId,
                    row.CaseKey,
                    suite.SuiteKey,
                    CaseStatusOf(row.Status),
                    row.Required))
                .ToArray())
        {
            AdvisoryOnly = plan.IsFork,
        };

        return new LoadedEvaluationInputs(
            evidence,
            PolicyInput(policy.Body, policy.Id),
            policy.Id,
            policy.Hash,
            revision.ManifestHash,
            revision.CreatedAt,
            plan.ProjectId,
            plan.Purpose == ReplayPurpose);
    }

    /// <summary>The run's plan, or the refusal a run nobody can judge deserves.</summary>
    private async ValueTask<EvaluationRunPlan> RequirePlanAsync(
        Guid orgId,
        Guid runId,
        CancellationToken cancellationToken) =>
        await plans.GetEvaluationRunPlanAsync(orgId, runId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "the run has no manifest or pinned suite, so its evidence cannot be judged");

    /// <summary>The pinned suite's facts, or the refusal a run whose suite the catalogue cannot answer deserves.</summary>
    private async ValueTask<EvaluationSuite> RequireSuiteAsync(
        Guid orgId,
        Guid suiteRevisionId,
        CancellationToken cancellationToken) =>
        await catalog.GetEvaluationSuiteAsync(orgId, suiteRevisionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "the run has no manifest or pinned suite, so its evidence cannot be judged");

    /// <summary>
    /// The frozen revision the task names with its admitted attempt statuses, or the refusal a revision nobody stored
    /// deserves.
    /// </summary>
    private async ValueTask<FrozenEvidenceInput> RequireFrozenAsync(
        Guid orgId,
        Guid runId,
        int evidenceRevision,
        CancellationToken cancellationToken) =>
        await frozenEvidence.GetFrozenInputAsync(orgId, runId, evidenceRevision, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the run has no evidence revision {evidenceRevision}, so there is nothing to evaluate");

    /// <summary>The run's pinned policy revision, or the refusal a verdict naming rules nobody stored deserves.</summary>
    private async ValueTask<PolicyRow> RequirePolicyAsync(
        Guid orgId,
        Guid policyRevisionId,
        CancellationToken cancellationToken) =>
        await db.QuerySingleOrDefaultAsync<PolicyRow>(
            EvaluationSql.LoadPolicy,
            new { org_id = orgId, policy_revision_id = policyRevisionId },
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the run's pinned policy revision {policyRevisionId:D} does not exist, so the verdict would name "
                + "rules nobody stored");

    /// <summary>
    /// One row per planned execution that had an attempt when this revision froze, in the <c>execution_key</c> order the
    /// read has always answered in. An execution whose every attempt postdates the freeze, or whose case revision the
    /// catalogue cannot resolve, is left out on purpose: the evaluator derives <c>missing_result</c> from the planned
    /// count it was given, so a synthesised row here would report a decision the evidence never made.
    /// </summary>
    private async ValueTask<ResultRow[]> ResultsAsync(
        Guid orgId,
        IReadOnlyList<FrozenAttemptStatus> attempts,
        IReadOnlyList<EvaluationPlannedExecution> executions,
        CancellationToken cancellationToken)
    {
        var cases = await catalog.GetEvaluationCasesAsync(
            orgId,
            CaseRevisionIdsOf(executions),
            cancellationToken).ConfigureAwait(false);

        // Keyed by the execution key, so a plan and a status list that arrive in any order still meet on the key.
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attempt in attempts)
        {
            statuses[attempt.ExecutionKey] = attempt.Status;
        }

        return
        [
            .. executions
                .Where(execution => statuses.ContainsKey(execution.ExecutionKey))
                .OrderBy(execution => execution.ExecutionKey, StringComparer.Ordinal)
                .Select(execution => cases.TryGetValue(execution.CaseRevisionId, out var identity)
                    ? new ResultRow(
                        identity.CaseId,
                        identity.CaseKey,
                        statuses[execution.ExecutionKey],
                        execution.Required)
                    : null)
                .OfType<ResultRow>(),
        ];
    }

    /// <summary>The distinct case revisions the plan names: the batch the catalogue read takes.</summary>
    private static Guid[] CaseRevisionIdsOf(IReadOnlyList<EvaluationPlannedExecution> executions) =>
        [.. executions.Select(execution => execution.CaseRevisionId).Distinct()];

    /// <summary>
    /// The revision's own missing codes. Anything short of <c>complete</c> that names no code is still not evidence the
    /// evaluator may read as complete, so it is judged on <see cref="MissingReasonCode.MissingResult"/> — the one code
    /// that says the evidence cannot account for what the run planned.
    /// </summary>
    private static MissingReasonCode[] MissingCodesOf(FrozenEvidenceRevision revision)
    {
        var codes = revision.Missing.Select(MissingCode).ToArray();
        return revision.Completeness != CompleteCompleteness && codes.Length == 0
            ? [MissingReasonCode.MissingResult]
            : codes;
    }

    /// <summary>
    /// The pinned revision's decision configuration, read from the body the schema validated as an object on write. A key
    /// the policy omits means "nothing extra is required", which is why every list defaults to empty. A body whose
    /// values this reader cannot parse raises naming the revision, because the verdict would otherwise be decided
    /// against a policy nobody can read back.
    /// </summary>
    private static PolicyRevisionInput PolicyInput(string body, Guid policyRevisionId)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            return new PolicyRevisionInput(
                requiredCaseKeys: Strings(root, "required_cases"),
                requiredSuiteKeys: Strings(root, "required_suites"),
                criticalCaseIds: Guids(root, "critical_cases"),
                allowedSkipCaseKeys: Strings(root, "allowed_skips"),
                flakyBudget: root.TryGetProperty("flaky_budget", out var budget) ? budget.GetInt32() : 0,
                requiredArtifactKinds: Strings(root, "required_artifacts"),
                // The v1 body carries no empty-suite allowance, so the policy half of the allowance is always false here:
                // a catalog-declared empty suite never passes until a policy revision can say it may.
                emptySuiteAllowed: false);
        }
        catch (Exception failure) when (
            failure is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            throw new InvalidOperationException(
                $"policy revision {policyRevisionId:D} carries a body this evaluator cannot read: {failure.Message}",
                failure);
        }
    }

    private static string[] Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var values)
            ? [.. values.EnumerateArray().Select(value => value.GetString() ?? string.Empty)]
            : [];

    private static Guid[] Guids(JsonElement root, string name) =>
        [.. Strings(root, name).Select(Guid.Parse)];

    /// <summary>
    /// A stored <c>missing[]</c> code. The column's CHECK admits only the evaluator's own vocabulary, so a value outside
    /// it is a database the code no longer understands and raises rather than silently dropping a reason.
    /// </summary>
    private static MissingReasonCode MissingCode(string token) => token switch
    {
        "missing_shard" => MissingReasonCode.MissingShard,
        "count_mismatch" => MissingReasonCode.CountMismatch,
        "artifact_missing" => MissingReasonCode.ArtifactMissing,
        "build_mismatch" => MissingReasonCode.BuildMismatch,
        "unknown_status" => MissingReasonCode.UnknownStatus,
        "setup_error" => MissingReasonCode.SetupError,
        "stale_epoch" => MissingReasonCode.StaleEpoch,
        "missing_result" => MissingReasonCode.MissingResult,
        _ => throw new InvalidOperationException($"the evidence revision carries the unknown missing code '{token}'"),
    };

    /// <summary>A stored normalized attempt status, by the same closed-vocabulary rule as <see cref="MissingCode"/>.</summary>
    private static CaseStatus CaseStatusOf(string token) => token switch
    {
        "passed" => CaseStatus.Passed,
        "failed" => CaseStatus.Failed,
        "skipped" => CaseStatus.Skipped,
        "timed_out" => CaseStatus.TimedOut,
        "error" => CaseStatus.Error,
        "blocked" => CaseStatus.Blocked,
        _ => throw new InvalidOperationException($"the attempt carries the unknown normalized status '{token}'"),
    };
}
