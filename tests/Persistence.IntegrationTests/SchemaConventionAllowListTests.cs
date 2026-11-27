using Shouldly;
using Xunit;

namespace Persistence.IntegrationTests;

/// <summary>
/// The allow-list of <see cref="SchemaConventionScan"/> is the one place where a
/// rule can be excused, so it is asserted offline and exactly: an entry added,
/// widened or dropped fails here rather than silently stopping a rule for a real
/// table. <c>rls_probe</c> is the only non-mechanism entry that predates the shared
/// trigger function (0003 is checksum-locked), and <c>role_permission</c> is the only
/// entry excused from a single rule for a reason a migration cannot fix (a built-in
/// role carries <c>org_id IS NULL</c>, so its composite tenant FK cannot exist).
/// </summary>
public sealed class SchemaConventionAllowListTests
{
    /// <summary>The pinned exemptions, in the scan's own order.</summary>
    private static readonly (string Table, SchemaRule Rules)[] Expected =
    [
        ("public.schema_migration", SchemaRule.All),
        ("public.outbox", SchemaRule.RowLevelSecurity | SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.task", SchemaRule.TenantKey | SchemaRule.CompositeForeignKey | SchemaRule.RowLevelSecurity),
        ("public.audit_log", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.case_identity_source", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.case_rename_history", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.test_revision", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.suite_revision", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.variant", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.suite_member", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.case_dependency", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.manifest", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.environment_revision", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.policy_revision", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.adapter_capability", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.planned_execution", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.case_attempt", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.event", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.event_batch", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.authoring_version", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.authoring_code_counter", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.rls_probe", SchemaRule.VersionTrigger),
        ("public.role_permission", SchemaRule.CompositeForeignKey),
        ("public.membership_role", SchemaRule.CompositeForeignKey),
        ("public.usage_ledger_entry", SchemaRule.CompositeForeignKey),
        ("public.evidence_revision", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.evaluation", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.project_ai_settings", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.ai_settings_template", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.manual_case_link", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.masking_event", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.report_parser", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.template", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.release_component", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.release_run", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.fixture", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.fixture_revision", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.requirement_version", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.ai_credit_entry", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.raw_report", SchemaRule.CompositeForeignKey | SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.raw_report_reprocess", SchemaRule.CompositeForeignKey | SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.run_state_event", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.platform_operator_audit", SchemaRule.VersionTrigger),
        ("public.incident_attempt", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
        ("public.release_decision", SchemaRule.AuditColumns | SchemaRule.VersionTrigger),
    ];

    [Fact]
    public void AllowList_Is_Exactly_The_Documented_Exemptions()
    {
        SchemaConventionScan.Exemptions
            .Select(entry => (entry.Table, entry.Rules))
            .ShouldBe(Expected);

        // Every exclusion carries its reason: an exemption without one is a silencer,
        // and the next reader cannot tell whether it is still needed.
        SchemaConventionScan.Exemptions.ShouldAllBe(entry => entry.Reason.Length > 0);

        // `outbox` keeps its tenant key: TL T3 lists only `schema_migration` and the
        // task queue as 01's exceptions to `unique (org_id, id)`.
        SchemaConventionScan.Exemptions.ShouldContain(
            entry => entry.Table == "public.outbox" && (entry.Rules & SchemaRule.TenantKey) == 0);

        // `role_permission` is excused from exactly one rule: its tenant key, RLS flags
        // and audit columns are checked like any other business table.
        SchemaConventionScan.Exemptions.ShouldContain(
            entry => entry.Table == "public.role_permission" && entry.Rules == SchemaRule.CompositeForeignKey);
    }
}
