namespace Evidgate.Modules.Evidence.Infrastructure.Persistence;

/// <summary>
/// The SQL of the masking scan (06.2.3 §4): one statement writes every event the scan found, inside the write
/// transaction the handler opens. The rows travel as one <c>jsonb</c> document expanded with
/// <c>jsonb_to_recordset</c>, because the tenant Dapper path refuses array parameters; the org and the artifact are
/// bound explicitly, and row level security applies as well.
/// </summary>
internal static class MaskingEventSql
{
    /// <summary>
    /// Writes one event per pattern class the scan matched, and nothing for a class that already has one: the natural
    /// key <c>(org_id, artifact_id, pattern_class)</c> is the replay guard the plan fixes (AC6), so a retried attempt
    /// that reaches this statement after a committed one adds no second row and never rewrites the first finding.
    /// </summary>
    internal const string InsertEvents = """
        insert into public.masking_event (id, org_id, artifact_id, pattern_class, hit_count, action)
        select e.id, e.org_id, e.artifact_id, e.pattern_class, e.hit_count, e.action
          from jsonb_to_recordset(@events::jsonb)
            as e(id uuid, org_id uuid, artifact_id uuid, pattern_class text, hit_count integer, action text)
         where e.org_id = @org_id
           and e.artifact_id = @artifact_id
        on conflict (org_id, artifact_id, pattern_class) do nothing
        """;
}
