namespace Evidgate.Modules.Evidence.Infrastructure.Persistence;

/// <summary>
/// The read of <see cref="Application.Capture.CaptureOptionsRead"/>. It runs on the caller's tenant scope and names the
/// trusted <c>org_id</c> explicitly as well as relying on row level security, so another tenant's project yields no row
/// whatever the caller asks for. One statement serves every kind and both halves of the answer: the project's policy
/// rows and its declared rules travel in the same result, so a switch and the rules beside it come from one snapshot and
/// two reads of one project can never report a policy that never held at the same moment as those rules.
/// </summary>
internal static class CaptureOptionsSql
{
    /// <summary>
    /// The marker of a policy row in the union below; the second branch's rows are declared rules.
    /// </summary>
    internal const string PolicyRowKind = "policy";

    /// <summary>
    /// The union of the two row shapes, each branch filtered by the trusted <c>(org_id, project_id)</c> pair: a policy
    /// row carries the columns the kind-policy resolution needs to answer the effective capture switch (so the read
    /// never guesses which knobs matter) and a null rule; a rule row carries its type and name, ordered so the answer is
    /// stable without a sort in the caller. The <c>redaction_rule_kind_key</c> unique key starts
    /// <c>(org_id, project_id, …)</c>, so the rule branch is an index scan of the project and needs no index of its own.
    /// The outer order puts the policy rows first and groups each kind's rules by type then name.
    /// </summary>
    internal const string LoadOptions = """
        select o."RowKind",
               o."Kind",
               o."MaxBytes",
               o."MediaTypes",
               o."CaptureEnabled",
               o."DefaultSensitivity",
               o."RuleType",
               o."NamePattern"
          from (
                select 'policy' as "RowKind",
                       p.kind as "Kind",
                       p.max_bytes as "MaxBytes",
                       p.media_types as "MediaTypes",
                       p.capture_enabled as "CaptureEnabled",
                       p.default_sensitivity as "DefaultSensitivity",
                       null::text as "RuleType",
                       null::text as "NamePattern"
                  from public.artifact_kind_policy p
                 where p.org_id = @org_id
                   and p.project_id = @project_id
                union all
                select 'rule',
                       r.kind,
                       null::bigint,
                       null::text[],
                       null::boolean,
                       null::text,
                       r.rule_type,
                       r.name_pattern
                  from public.redaction_rule r
                 where r.org_id = @org_id
                   and r.project_id = @project_id
               ) o
         order by o."RowKind", o."Kind", o."RuleType", o."NamePattern"
        """;
}
