namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The coverage view of one requirement as a single statement. A READ COMMITTED transaction takes one snapshot per
/// statement, so the head, the counts, the targets and the page of cases are only coherent when one statement reads
/// them all: separate reads could show a head from before a concurrent append next to links pinned after it. The
/// statement names the organization, the project and the requirement.
/// </summary>
internal static class RequirementCoverageSql
{
    /// <summary>
    /// One row: the head (id and number), the counts of the whole requirement (not of a page), the pinned targets in
    /// creation order (link ids are time-ordered; <c>@take_targets</c> is one more than the caller lists) and one page of
    /// distinct live cases keyed by the project-unique code (<c>@take_cases</c> is one more than the page). No row means
    /// the requirement is not in this project. A case is reached by its own link or through a live linked checklist item
    /// (a soft-deleted item reaches nothing); a case reached both ways is folded to one. The automated identities come
    /// from the manual-to-catalog links of the page's cases only.
    /// </summary>
    internal const string Coverage = """
        with req as (
          select r.id, r.head_version_no, h.id as head_version_id
            from public.requirement r
            join public.requirement_version h
              on h.org_id = r.org_id and h.requirement_id = r.id and h.version_no = r.head_version_no
           where r.org_id = @org_id and r.project_id = @project_id and r.id = @requirement_id
        ),
        links as (
          select l.id, l.requirement_version_id, l.checklist_item_id, l.manual_test_case_id, v.version_no,
                 v.version_no <> req.head_version_no as stale
            from req
            join public.requirement_link l
              on l.org_id = @org_id and l.project_id = @project_id and l.requirement_id = req.id
            join public.requirement_version v
              on v.org_id = l.org_id and v.requirement_id = l.requirement_id and v.id = l.requirement_version_id
        ),
        reached as (
          select l.manual_test_case_id as case_id, true as direct, false as via_item
            from links l
           where l.manual_test_case_id is not null
          union all
          select m.id, false, true
            from links l
            join public.checklist_item ci
              on ci.org_id = @org_id and ci.project_id = @project_id and ci.id = l.checklist_item_id
             and ci.deleted_at is null
            join public.manual_test_case m
              on m.org_id = @org_id and m.project_id = @project_id and m.checklist_item_id = l.checklist_item_id
           where l.checklist_item_id is not null
        ),
        live as (
          select m.id as case_id, m.code, m.title, m.state, m.checklist_item_id, p.direct, p.via_item
            from (select case_id, bool_or(direct) as direct, bool_or(via_item) as via_item
                    from reached
                   group by case_id) p
            join public.manual_test_case m
              on m.org_id = @org_id and m.project_id = @project_id and m.id = p.case_id and m.deleted_at is null
        )
        select req.head_version_id, req.head_version_no,
               (select count(*)::integer from live) as linked_case_count,
               (select count(*)::integer from live where state = 'approved') as approved_case_count,
               (select count(*)::integer from links) as link_count,
               (select count(*)::integer from links where stale) as stale_link_count,
               coalesce((
                 select jsonb_agg(jsonb_build_object(
                          'link_id', t.id, 'version_id', t.requirement_version_id, 'version_no', t.version_no,
                          'checklist_item_id', t.checklist_item_id, 'manual_test_case_id', t.manual_test_case_id,
                          'stale', t.stale) order by t.id)
                   from (select * from links order by id limit @take_targets) t), '[]'::jsonb)::text as targets,
               coalesce((
                 select jsonb_agg(jsonb_build_object(
                          'case_id', c.case_id, 'code', c.code, 'title', c.title, 'state', c.state,
                          'checklist_item_id', c.checklist_item_id, 'direct', c.direct, 'via_item', c.via_item,
                          'automated_case_ids', to_jsonb(coalesce(a.test_case_ids, array[]::uuid[]))) order by c.code)
                   from (select * from live
                          where @after_code::text is null or code > @after_code
                          order by code
                          limit @take_cases) c
                   left join lateral (
                         select array_agg(x.test_case_id order by x.test_case_id) as test_case_ids
                           from public.manual_case_link x
                          where x.org_id = @org_id and x.project_id = @project_id and x.manual_test_case_id = c.case_id) a on true),
                 '[]'::jsonb)::text as cases
          from req
        """;
}
