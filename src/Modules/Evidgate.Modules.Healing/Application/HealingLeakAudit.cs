using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// The audit event of a call the redaction gate refused. The gateway runs outside any pipeline command, so it writes
/// the entry itself, in the transaction that records the refused call: the row, its report and the entry commit
/// together or not at all. The entry names the rule, the segment and the report digest and nothing of the payload.
/// </summary>
internal static class HealingLeakAudit
{
    /// <summary>The action string; row 79 of the audit taxonomy.</summary>
    internal const string Action = "healing_llm.blocked_leak";

    /// <summary>The object kind: the <c>healing_llm_call</c> row that records the refusal.</summary>
    internal const string ObjectType = "healing_llm_call";

    private const string BudgetRule = "redaction_budget_exceeded";
    private const string Unclassified = "residual_unclassified";
    private const string SegmentPrefix = "segment:";

    /// <summary>
    /// The metadata of the entry: the first rule that still matched (or the cost budget), its 1-based segment ordinal
    /// (0 outside a segment) and the report digest. A blocked outcome with no residual finding is still recorded, under
    /// <c>residual_unclassified</c>, so a pipeline that blocks without saying why cannot make the refusal disappear.
    /// </summary>
    /// <param name="report">The report of the blocked call.</param>
    internal static IReadOnlyDictionary<string, string> Metadata(RedactionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var hit = report.Findings.FirstOrDefault(IsBlocking);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rule_id"] = hit?.RuleId ?? Unclassified,
            ["segment_ordinal"] = Ordinal(hit?.FieldPath).ToString(CultureInfo.InvariantCulture),
            ["report_hash"] = report.Hash,
        };
    }

    /// <summary>Appends the entry to the org's chain inside the open tenant transaction of <paramref name="db"/>.</summary>
    /// <param name="audit">The audit buffer.</param>
    /// <param name="db">The tenant database of the transaction that records the call.</param>
    /// <param name="orgId">The org whose chain is extended.</param>
    /// <param name="projectId">The project the call belongs to; a tag for the project filter.</param>
    /// <param name="callId">The recorded call.</param>
    /// <param name="report">The report of the blocked call.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async Task WriteAsync(
        IAuditTrail audit,
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        Guid callId,
        RedactionReport report,
        CancellationToken cancellationToken)
    {
        // The metadata is validated here, before the unit opens, so a key or value the allowlist refuses fails the call.
        var metadata = Metadata(report);

        // BeginUnit joins an open unit rather than opening a second, and the flush clears the buffer: the entry is
        // appended exactly once whichever caller opened the unit.
        using var unit = audit.BeginUnit();
        audit.Describe(new AuditObject(ObjectType, callId.ToString("D"), Metadata: metadata, ProjectId: projectId));
        audit.Record(Action, new AuditActor(AuditActorTypes.System, "system"));
        await audit.FlushAsync(orgId, db, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsBlocking(RedactionFinding finding) =>
        finding.RuleId.StartsWith("residual_", StringComparison.Ordinal)
        || string.Equals(finding.RuleId, BudgetRule, StringComparison.Ordinal);

    private static int Ordinal(string? fieldPath) =>
        fieldPath is not null
        && fieldPath.StartsWith(SegmentPrefix, StringComparison.Ordinal)
        && int.TryParse(fieldPath.AsSpan(SegmentPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
            ? ordinal
            : 0;
}
