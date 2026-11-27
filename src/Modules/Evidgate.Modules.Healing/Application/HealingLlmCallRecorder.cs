using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Healing.Contracts;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// The one statement that attributes an attempt (AC4, AC7). One row per attempt — a schema violation that is
/// retried leaves two rows, so the spend of the refused answer is not lost — written inside the caller's tenant
/// transaction through the module's Dapper path (<see cref="ITenantDb"/> over the ambient scope, the
/// <see cref="HealingSwitchStore"/> shape), so RLS scopes it to the caller's org and a rollback takes the rows with it.
/// </summary>
/// <remarks>
/// The statement names its columns explicitly and never a wildcard: the table has no prompt, response, secret or key
/// column (AC6), and this is the only writer, so there is no path by which a payload body could reach a row. The first
/// attempt of a call also writes the call's redaction report, in the same statement, so a call row never names a report
/// hash that has no report behind it; later attempts of the same call carry the same hash.
/// </remarks>
internal sealed class HealingLlmCallRecorder(ITenantDb db, ITenantScopeAccessor scopes, IAuditTrail audit)
{
    /// <summary>Records one attempt and returns the row id (the value a generation stores as its <c>ai_run_id</c>).</summary>
    /// <param name="request">The call the attempt belongs to; its attribution columns are copied verbatim.</param>
    /// <param name="attempt">The 1-based attempt number of this row.</param>
    /// <param name="status">One of <see cref="HealingLlmStatuses"/>.</param>
    /// <param name="tokens">Tokens this attempt spent.</param>
    /// <param name="cost">Cost this attempt incurred.</param>
    /// <param name="redaction">What the redaction pipeline did to this call's payload.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<Guid> RecordAsync(
        HealingLlmRequest request,
        int attempt,
        string status,
        int tokens,
        decimal cost,
        RedactionReport redaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(redaction);
        var id = Guid.CreateVersion7();
        var orgId = scopes.Require().OrgId;
        var reportJson = redaction.ToJson();

        await db.ExecuteAsync(
                attempt == 1 ? HealingLlmCallSql.InsertWithReport : HealingLlmCallSql.Insert,
                new
                {
                    id,
                    org_id = orgId,
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
                    report_id = Guid.CreateVersion7(),
                    rule_version = redaction.RuleVersion,
                    report = reportJson,
                    report_hash = RedactionReport.HashOf(reportJson),
                },
                cancellationToken)
            .ConfigureAwait(false);

        // A refusal is the one outcome that is also an audit event; it commits with the row and its report, so a
        // call that was refused always has its entry and a rolled-back one has neither.
        if (status == HealingLlmStatuses.BlockedLeak)
        {
            await HealingLeakAudit
                .WriteAsync(audit, db, orgId, request.ProjectId, id, redaction, cancellationToken)
                .ConfigureAwait(false);
        }

        return id;
    }
}

/// <summary>The statements of <see cref="HealingLlmCallRecorder"/> (migrations 0054 and 0136).</summary>
internal static class HealingLlmCallSql
{
    /// <summary>The append-only attempt insert for an attempt that follows the first of its call.</summary>
    internal const string Insert = """
        insert into public.healing_llm_call
          (id, org_id, project_id, incident_id, candidate_id, purpose, model, prompt_version, tokens, cost, status, attempt,
           redaction_report_hash)
        values
          (@id, @org_id, @project_id, @incident_id, @candidate_id, @purpose, @model, @prompt_version, @tokens, @cost, @status,
           @attempt, @report_hash)
        """;

    /// <summary>
    /// The first attempt: the call's redaction report and the attempt row in one statement, so both commit or neither does.
    /// </summary>
    internal const string InsertWithReport = """
        with report as (
          insert into public.healing_redaction_report
            (id, org_id, project_id, incident_id, candidate_id, purpose, rule_version, report, report_hash)
          values
            (@report_id, @org_id, @project_id, @incident_id, @candidate_id, @purpose, @rule_version, @report::jsonb, @report_hash)
        )
        insert into public.healing_llm_call
          (id, org_id, project_id, incident_id, candidate_id, purpose, model, prompt_version, tokens, cost, status, attempt,
           redaction_report_hash)
        values
          (@id, @org_id, @project_id, @incident_id, @candidate_id, @purpose, @model, @prompt_version, @tokens, @cost, @status,
           @attempt, @report_hash)
        """;
}
