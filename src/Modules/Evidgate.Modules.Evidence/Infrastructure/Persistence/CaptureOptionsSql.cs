namespace Evidgate.Modules.Evidence.Infrastructure.Persistence;

/// <summary>
/// The reads of <see cref="Application.Capture.CaptureOptionsRead"/>. Both run on the caller's tenant scope and name the
/// trusted <c>org_id</c> explicitly as well as relying on row level security, so another tenant's project yields no row
/// whatever the caller asks for. Two statements serve every kind: one for the project's policy rows and one for its
/// declared rules, never one per kind.
/// </summary>
internal static class CaptureOptionsSql
{
    /// <summary>
    /// The project's policy rows, one per kind it overrides. The columns are the ones the kind-policy resolution needs to
    /// answer the effective capture switch, so the read never guesses which knobs matter.
    /// </summary>
    internal const string LoadPolicies = """
        select p.kind as "Kind",
               p.max_bytes as "MaxBytes",
               p.media_types as "MediaTypes",
               p.capture_enabled as "CaptureEnabled",
               p.default_sensitivity as "DefaultSensitivity"
          from public.artifact_kind_policy p
         where p.org_id = @org_id
           and p.project_id = @project_id
        """;

    /// <summary>
    /// Every rule the project declared, ordered so the answer is stable without a sort in the caller. The
    /// <c>redaction_rule_kind_key</c> unique key starts <c>(org_id, project_id, …)</c>, so this read is an index scan of the
    /// project and needs no index of its own.
    /// </summary>
    internal const string LoadRules = """
        select r.kind as "Kind",
               r.rule_type as "RuleType",
               r.name_pattern as "NamePattern"
          from public.redaction_rule r
         where r.org_id = @org_id
           and r.project_id = @project_id
         order by r.kind, r.rule_type, r.name_pattern
        """;
}
