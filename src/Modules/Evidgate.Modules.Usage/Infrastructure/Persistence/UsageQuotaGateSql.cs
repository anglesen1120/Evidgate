namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// The <c>quota_counter</c> statements, in one place so the gate, the raise path and the tests share the
/// served text instead of a copy.
/// <para>
/// Every statement names the scope through the sentinel <c>coalesce(project_id, …)</c> triple the unique
/// index uses, so one index probe answers it and an org-level counter (null project) is a plain equality
/// rather than an <c>is not distinct from</c> the planner cannot index.
/// </para>
/// </summary>
internal static class UsageQuotaGateSql
{
    /// <summary>
    /// The reservation: one conditional increment. The row is touched only while <c>used + @quantity</c>
    /// stays within the limit, so concurrent batches serialise on the row lock and the last unit is
    /// granted once — the statement is the check, never a read followed by a write.
    /// </summary>
    internal const string Increment = """
        update public.quota_counter as c
        set used = c.used + @quantity,
            warned_at = case
                when c.warned_at is null and c.used + @quantity >= c.quota_limit * c.warn_ratio then now()
                else c.warned_at
            end
        where c.org_id = @org_id
          and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid)
              = coalesce(@project_id::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
          and c.period = @period
          and c.metric = @metric
          and c.used + @quantity <= c.quota_limit
        returning c.used, c.quota_limit, c.warn_ratio
        """;

    /// <summary>
    /// The refusal stamp of a call that found no room: it records when the scope was first blocked and
    /// leaves <c>used</c> untouched, because a refused quantity was never counted. It answers how many
    /// rows it stamped — zero means the scope has no counter at all.
    /// </summary>
    internal const string Block = """
        update public.quota_counter as c
        set blocked_at = coalesce(c.blocked_at, now())
        where c.org_id = @org_id
          and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid)
              = coalesce(@project_id::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
          and c.period = @period
          and c.metric = @metric
        """;

    /// <summary>
    /// The raise: one upsert on the scope's unique key. A scope with no counter gains one holding no
    /// usage; an existing one moves only upwards, so a raise can never put the limit below what the period
    /// already used. Clearing <c>blocked_at</c> is the counter's state transition; <c>warned_at</c> stays,
    /// so a scope that once warned still says so.
    /// </summary>
    internal const string Raise = """
        insert into public.quota_counter as c (
            id, org_id, project_id, period, metric, used, quota_limit)
        values (uuidv7(), @org_id, @project_id, @period, @metric, 0, @quota_limit)
        on conflict (org_id, coalesce(project_id, '00000000-0000-0000-0000-000000000000'::uuid), period, metric)
        do update set
            quota_limit = excluded.quota_limit,
            blocked_at = case when excluded.quota_limit > c.used then null else c.blocked_at end
        where excluded.quota_limit > c.quota_limit
        returning c.id, c.used, c.quota_limit
        """;

    /// <summary>The scope's counter, or no row when the org holds none (no limit configured).</summary>
    internal const string Read = """
        select c.used, c.quota_limit, c.warn_ratio
        from public.quota_counter as c
        where c.org_id = @org_id
          and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid)
              = coalesce(@project_id::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
          and c.period = @period
          and c.metric = @metric
        """;
}
