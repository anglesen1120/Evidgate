namespace Evidgate.Modules.Policy.Infrastructure.Persistence;

/// <summary>The one read of the exceptions snapshot: which waivers and quarantine entries were in force at an instant.</summary>
/// <remarks>
/// Both tables are owned by this module, so the read stays inside it. The instant is a parameter, never <c>now()</c>:
/// the caller captures it once, and a statement that read the clock itself could disagree with the instant the row stores.
/// A row whose <c>expires_at</c> has passed is excluded even when the sweeper has not yet moved its state, because the
/// sweeper runs on a schedule and the decision must not wait for it.
/// </remarks>
internal static class ExceptionsSnapshotSql
{
    /// <summary>The active waiver ids (<c>kind = 'waiver'</c>) and quarantine entry ids of one project at <c>@as_of</c>.</summary>
    internal const string LoadActive = """
        select 'waiver' as "Kind", w.id as "Id"
          from public.waiver w
         where w.org_id = @org_id
           and w.project_id = @project_id
           and w.state = 'approved'
           and w.expires_at > @as_of
        union all
        select 'quarantine' as "Kind", q.id as "Id"
          from public.quarantine_entry q
         where q.org_id = @org_id
           and q.project_id = @project_id
           and q.state = 'active'
           and q.expires_at > @as_of
        """;
}
