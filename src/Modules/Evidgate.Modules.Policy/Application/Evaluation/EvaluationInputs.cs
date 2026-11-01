using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Evaluation;
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
internal sealed record LoadedEvaluationInputs(
    EvidenceRevisionInput Evidence,
    PolicyRevisionInput Policy,
    Guid PolicyRevisionId,
    string PolicyHash,
    string ManifestHash,
    DateTimeOffset FrozenAt);

/// <summary>
/// Builds <see cref="LoadedEvaluationInputs"/> from the run's own rows: four reads on the caller's tenant
/// scope, so row level security decides what an attempt can see and nothing is taken from the task payload but the
/// ids it names.
/// </summary>
/// <remarks>
/// A run that pins no policy revision, a revision number that does not exist, a pinned policy revision that does not
/// exist and a policy body this reader cannot parse all raise: the evaluator would otherwise judge evidence against
/// rules nobody pinned, and a retry cannot make a missing row appear, so the exception is what an operator sees in the
/// dead-letter list.
/// </remarks>
internal sealed class EvaluationInputReader(ITenantDb db, ITenantScopeAccessor scopes)
{
    /// <summary>The completeness token a revision must carry for its <c>missing[]</c> to speak for itself.</summary>
    private const string CompleteCompleteness = "complete";

    /// <summary>The planned executions, the pinned policy revision and the pinned suite's key.</summary>
    private sealed record RunPlanRow(Guid? PolicyRevisionId, int PlannedCount, string SuiteKey, bool SuiteDeclaredEmpty);

    /// <summary>The frozen revision's own verdict inputs and freeze stamp.</summary>
    private sealed record RevisionRow(
        int RevisionNo,
        string Completeness,
        string[] Missing,
        string ManifestHash,
        DateTimeOffset FrozenAt);

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

        var plan = await db.QuerySingleOrDefaultAsync<RunPlanRow>(
            EvaluationSql.LoadRun,
            new { org_id = orgId, run_id = runId },
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "the run has no manifest or pinned suite, so its evidence cannot be judged");

        var policyRevisionId = plan.PolicyRevisionId
            ?? throw new InvalidOperationException(
                "run pins no policy revision, so there are no rules to judge its evidence against");

        var revision = await db.QuerySingleOrDefaultAsync<RevisionRow>(
            EvaluationSql.LoadRevision,
            new { org_id = orgId, run_id = runId, evidence_revision = evidenceRevision },
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the run has no evidence revision {evidenceRevision}, so there is nothing to evaluate");

        var policy = await db.QuerySingleOrDefaultAsync<PolicyRow>(
            EvaluationSql.LoadPolicy,
            new { org_id = orgId, policy_revision_id = policyRevisionId },
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the run's pinned policy revision {policyRevisionId:D} does not exist, so the verdict would name "
                + "rules nobody stored");

        var results = await db.QueryAsync<ResultRow>(
            EvaluationSql.LoadResults,
            new { org_id = orgId, run_id = runId, evidence_revision = evidenceRevision },
            cancellationToken).ConfigureAwait(false);

        var evidence = new EvidenceRevisionInput(
            plan.PlannedCount,
            plan.SuiteDeclaredEmpty,
            MissingCodesOf(revision),
            results
                .Select(row => new CaseResultInput(
                    row.CaseId,
                    row.CaseKey,
                    plan.SuiteKey,
                    CaseStatusOf(row.Status),
                    row.Required))
                .ToArray());

        return new LoadedEvaluationInputs(
            evidence,
            PolicyInput(policy.Body, policy.Id),
            policy.Id,
            policy.Hash,
            revision.ManifestHash,
            revision.FrozenAt);
    }

    /// <summary>
    /// The revision's own missing codes. Anything short of <c>complete</c> that names no code is still not evidence the
    /// evaluator may read as complete, so it is judged on <see cref="MissingReasonCode.MissingResult"/> — the one code
    /// that says the evidence cannot account for what the run planned.
    /// </summary>
    private static MissingReasonCode[] MissingCodesOf(RevisionRow revision)
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
