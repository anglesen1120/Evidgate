using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Healing.Contracts;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// 08.5.1-T1: the one statement that attributes an attempt (AC4, AC7). One row per attempt — a schema violation that is
/// retried leaves two rows, so the spend of the refused answer is not lost — written inside the caller's tenant
/// transaction through the module's Dapper path (<see cref="ITenantDb"/> over the ambient scope, the
/// <see cref="HealingSwitchStore"/> shape), so RLS scopes it to the caller's org and a rollback takes the rows with it.
/// </summary>
/// <remarks>
/// The statement names its columns explicitly and never a wildcard: the table has no prompt, response, secret or key
/// column (AC6), and this is the only writer, so there is no path by which a payload body could reach a row.
/// </remarks>
internal sealed class HealingLlmCallRecorder(ITenantDb db, ITenantScopeAccessor scopes)
{
    /// <summary>Records one attempt and returns the row id (the value a generation stores as its <c>ai_run_id</c>).</summary>
    /// <param name="request">The call the attempt belongs to; its attribution columns are copied verbatim.</param>
    /// <param name="attempt">The 1-based attempt number of this row.</param>
    /// <param name="status">One of <see cref="HealingLlmStatuses"/>.</param>
    /// <param name="tokens">Tokens this attempt spent.</param>
    /// <param name="cost">Cost this attempt incurred.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<Guid> RecordAsync(
        HealingLlmRequest request,
        int attempt,
        string status,
        int tokens,
        decimal cost,
        CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();

        await db.ExecuteAsync(
                HealingLlmCallSql.Insert,
                new
                {
                    id,
                    org_id = scopes.Require().OrgId,
                    project_id = request.ProjectId,
                    incident_id = request.IncidentId,
                    candidate_id = request.CandidateId,
                    purpose = request.Purpose,
                    model = request.Model,
                    prompt_version = request.PromptVersion,
                    tokens,
                    cost,
                    status,
                    attempt,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return id;
    }
}

/// <summary>The statements of <see cref="HealingLlmCallRecorder"/> (migration 0054).</summary>
internal static class HealingLlmCallSql
{
    /// <summary>
    /// The append-only insert. <c>redaction_report_hash</c> is left to its default: the hash of the redaction report is
    /// 08.5.3's, which wraps this seam rather than being assumed by it.
    /// </summary>
    internal const string Insert = """
        insert into public.healing_llm_call
          (id, org_id, project_id, incident_id, candidate_id, purpose, model, prompt_version, tokens, cost, status, attempt)
        values
          (@id, @org_id, @project_id, @incident_id, @candidate_id, @purpose, @model, @prompt_version, @tokens, @cost, @status, @attempt)
        """;
}
