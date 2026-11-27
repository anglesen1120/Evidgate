namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The set-based reads of the coverage view of one requirement: three statements, none of them run per case. Every
/// statement names the organization, the project and the requirement.
/// </summary>
internal static class RequirementCoverageSql
{
    /// <summary>
    /// The cases a requirement reaches, one row per (case, way): the case's own link, and every case of a linked
    /// checklist item. A case reached both ways appears twice here and is folded by <see cref="Paths"/>.
    /// </summary>
    private const string Reached = """
        reached as (
          select l.manual_test_case_id as case_id, true as direct, false as via_item
            from public.requirement_link l
           where l.org_id = @org_id and l.project_id = @project_id and l.requirement_id = @requirement_id
             and l.manual_test_case_id is not null
          union all
          select m.id, false, true
            from public.requirement_link l
            join public.manual_test_case m
              on m.org_id = l.org_id and m.project_id = l.project_id and m.checklist_item_id = l.checklist_item_id
           where l.org_id = @org_id and l.project_id = @project_id and l.requirement_id = @requirement_id
             and l.checklist_item_id is not null
        )
        """;

    /// <summary>One row per distinct case: the fold that keeps a case linked twice from counting twice.</summary>
    private const string Paths = """
        paths as (
          select case_id, bool_or(direct) as direct, bool_or(via_item) as via_item
            from reached
           group by case_id
        )
        """;

    /// <summary>
    /// The counts of the whole requirement, not of a page: distinct live cases reached, how many are approved, how many
    /// links there are and how many of them pin an older version than the head.
    /// </summary>
    internal const string Summary = $"""
        with {Reached},
        {Paths}
        select cc.linked_case_count, cc.approved_case_count, lc.link_count, lc.stale_link_count
          from (select count(*)::integer as linked_case_count,
                       (count(*) filter (where m.state = 'approved'))::integer as approved_case_count
                  from paths p
                  join public.manual_test_case m
                    on m.org_id = @org_id and m.project_id = @project_id and m.id = p.case_id and m.deleted_at is null) cc,
               (select count(*)::integer as link_count,
                       (count(*) filter (where v.version_no <> r.head_version_no))::integer as stale_link_count
                  from public.requirement_link l
                  join public.requirement r
                    on r.org_id = l.org_id and r.project_id = l.project_id and r.id = l.requirement_id
                  join public.requirement_version v
                    on v.org_id = l.org_id and v.requirement_id = l.requirement_id and v.id = l.requirement_version_id
                 where l.org_id = @org_id and l.project_id = @project_id and l.requirement_id = @requirement_id) lc
        """;

    /// <summary>
    /// The pinned targets in creation order (link ids are time-ordered), each with the version it pins and whether
    /// that is older than the head. Capped by the caller, who passes one more than it lists.
    /// </summary>
    internal const string Targets = """
        select l.id as link_id, l.requirement_version_id as version_id, v.version_no,
               l.checklist_item_id, l.manual_test_case_id,
               v.version_no <> r.head_version_no as stale
          from public.requirement_link l
          join public.requirement r
            on r.org_id = l.org_id and r.project_id = l.project_id and r.id = l.requirement_id
          join public.requirement_version v
            on v.org_id = l.org_id and v.requirement_id = l.requirement_id and v.id = l.requirement_version_id
         where l.org_id = @org_id and l.project_id = @project_id and l.requirement_id = @requirement_id
         order by l.id
         limit @take
        """;

    /// <summary>
    /// One page of the distinct live cases, keyed by the project-unique case code. The automated identities come from
    /// the manual-to-catalog links of the page's cases only (the catalog's own rows are not read: the id is the
    /// identity, and its status is the catalog contract's to answer).
    /// </summary>
    internal const string Cases = $"""
        with {Reached},
        {Paths}
        select m.id as case_id, m.code, m.title, m.state, m.checklist_item_id, p.direct, p.via_item,
               coalesce(a.test_case_ids, array[]::uuid[]) as automated_case_ids
          from paths p
          join public.manual_test_case m
            on m.org_id = @org_id and m.project_id = @project_id and m.id = p.case_id and m.deleted_at is null
          left join lateral (
                select array_agg(c.test_case_id order by c.test_case_id) as test_case_ids
                  from public.manual_case_link c
                 where c.org_id = m.org_id and c.project_id = m.project_id and c.manual_test_case_id = m.id) a on true
         where (@after_code::text is null or m.code > @after_code)
         order by m.code
         limit @take
        """;
}
