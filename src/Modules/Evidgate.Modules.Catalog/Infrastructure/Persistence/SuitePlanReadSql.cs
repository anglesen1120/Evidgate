namespace Evidgate.Modules.Catalog.Infrastructure.Persistence;

/// <summary>
/// The one Dapper statement of a suite execution-plan page: the revision's project, whether a continuation cursor's
/// anchor is a member of that revision, and the keyset page itself, so a page costs one statement and a revision the
/// tenant or project gate rejects yields no row at all. Every table carries the tenant predicate itself, so the page
/// never depends on the plan the planner would otherwise pick for its RLS qual.
/// </summary>
internal static class SuitePlanReadSql
{
    /// <summary>
    /// The revision row survives a page with no member (left join), which is what makes an empty revision an empty
    /// page, while a revision the tenant predicate or the project gate rejects leaves the CTE empty and so the result
    /// empty. <c>(ordinal, id)'s</c> strict order is the index <c>suite_member_plan_idx</c>'s order, so a row written
    /// between two pages sorts after the cursor and is neither repeated nor skipped.
    /// </summary>
    internal const string Page = """
        with revision as (
          select s.project_id
            from public.suite_revision r
            join public.suite s on s.org_id = r.org_id and s.id = r.suite_id
           where r.org_id = @org_id
             and r.id = @revision_id
        ),
        anchor as (
          select exists (
            select 1
              from public.suite_member a
             where a.org_id = @org_id
               and a.suite_revision_id = @revision_id
               and a.ordinal = @cursor_ordinal::integer
               and a.id = @cursor_id::uuid
          ) as present
        )
        select rv.project_id,
               an.present as anchor_present,
               m.case_revision_id,
               tr.case_id,
               v.variant_hash,
               m.ordinal,
               m.id
          from revision rv
          cross join anchor an
          left join public.suite_member m
            on m.org_id = @org_id
           and m.suite_revision_id = @revision_id
           and (m.ordinal, m.id) > (@cursor_ordinal::integer, @cursor_id::uuid)
          left join public.test_revision tr on tr.org_id = m.org_id and tr.id = m.case_revision_id
          left join public.variant v on v.org_id = m.org_id and v.id = m.variant_id
         order by m.ordinal,
                  m.id
         limit @limit
        """;
}
