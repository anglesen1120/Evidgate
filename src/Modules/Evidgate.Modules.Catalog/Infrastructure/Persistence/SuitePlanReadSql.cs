namespace Evidgate.Modules.Catalog.Infrastructure.Persistence;

/// <summary>
/// The one Dapper statement of a suite execution-plan page: the revision's project under both project restrictions,
/// whether a continuation cursor's anchor is a member of that revision, and the keyset page itself, so a page costs
/// one statement and a revision the tenant or project gate rejects yields no row — its member CTE is keyed to that
/// same revision and guarded by it, so no member row of such a revision can be returned. Every table carries its
/// tenant predicate itself, so the page never depends on the plan the planner picks for its RLS qual.
/// </summary>
internal static class SuitePlanReadSql
{
    /// <summary>
    /// The revision row survives a page with no member (left join), which is what makes an empty revision an empty
    /// page, while a revision the tenant predicate or a project gate rejects leaves the CTE empty and so the result
    /// empty; the member CTE is additionally guarded by <c>exists (select 1 from rev)</c>, so it cannot contribute a
    /// row for a rejected revision. The page is limited on its own keyset <c>(ordinal, id)</c> — the order of
    /// <c>suite_member_plan_idx</c> — so a row written between two pages sorts after the cursor and is neither repeated
    /// nor skipped; only the bounded page is sorted again for the revision's left join.
    /// </summary>
    internal const string Page = """
        with rev as (
          select s.project_id
            from public.suite_revision r
            join public.suite s on s.org_id = r.org_id and s.id = r.suite_id
           where r.org_id = @org_id
             and r.id = @revision_id
             and (@token_project_id::uuid is null or @token_project_id::uuid = s.project_id)
             and (@request_project_id::uuid is null or @request_project_id::uuid = s.project_id)
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
        ),
        page as (
          select m.case_revision_id,
                 tr.case_id,
                 v.variant_hash,
                 m.ordinal,
                 m.id
            from public.suite_member m
            join public.test_revision tr on tr.org_id = m.org_id and tr.id = m.case_revision_id
            join public.variant v on v.org_id = m.org_id and v.id = m.variant_id
           where m.org_id = @org_id
             and m.suite_revision_id = @revision_id
             and (m.ordinal, m.id) > (@cursor_ordinal::integer, @cursor_id::uuid)
             and exists (select 1 from rev)
           order by m.ordinal, m.id
           limit @limit
        )
        select rv.project_id,
               an.present as anchor_present,
               p.case_revision_id,
               p.case_id,
               p.variant_hash,
               p.ordinal,
               p.id
          from rev rv
          cross join anchor an
          left join page p on true
         order by p.ordinal, p.id
        """;
}
