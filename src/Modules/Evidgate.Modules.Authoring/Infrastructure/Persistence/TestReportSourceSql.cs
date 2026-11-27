namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The statements that read what a report is built from. Each source read is ONE statement: the tenant scope runs at
/// READ COMMITTED and cannot take a snapshot level after its first statement, so a single statement is the one
/// consistent point every source kind is read at. Every statement names the organization and the project, so a foreign
/// id matches no row on top of the row level security policy, and every kind is cut at one row over its cap so the job
/// can refuse a too large source instead of truncating it.
/// </summary>
internal static class TestReportSourceSql
{
    /// <summary>How many of the ids are live feature nodes of the project.</summary>
    internal const string CountFeatureNodes = """
        select count(*)::int
          from public.feature_node
         where org_id = @org_id and project_id = @project_id and deleted_at is null and id = any(@ids)
        """;

    /// <summary>How many of the ids are live requirements of the project.</summary>
    internal const string CountRequirements = """
        select count(*)::int
          from public.requirement
         where org_id = @org_id and project_id = @project_id and deleted_at is null and id = any(@ids)
        """;

    /// <summary>
    /// The selected feature nodes, everything below them and the path above them, with the live items, cases,
    /// requirements and links of that tree.
    /// </summary>
    internal const string ReadFeatureNodes = """
        with recursive down as (
          select n.id from public.feature_node n
           where n.org_id = @org_id and n.project_id = @project_id and n.deleted_at is null and n.id = any(@ids)
          union
          select c.id from public.feature_node c
            join down d on c.parent_id = d.id
           where c.org_id = @org_id and c.project_id = @project_id and c.deleted_at is null
        ),
        tree as (select id from down limit @max_rows + 1),
        items as (
          select i.id, i.feature_node_id, i.title, i.description, i.priority, i.state
            from public.checklist_item i
           where i.org_id = @org_id and i.project_id = @project_id and i.deleted_at is null
             and i.feature_node_id in (select id from tree)
           order by i.id limit @max_rows + 1
        ),
        cases as (
          select c.id, c.checklist_item_id, c.code, c.title, c.description, c.precondition, c.steps, c.priority, c.state
            from public.manual_test_case c
           where c.org_id = @org_id and c.project_id = @project_id and c.deleted_at is null
             and c.checklist_item_id in (select id from items)
           order by c.id limit @max_rows + 1
        ),
        links as (
          select l.id, l.requirement_id, l.requirement_version_id, v.version_no, l.checklist_item_id, l.manual_test_case_id
            from public.requirement_link l
            join public.requirement_version v
              on v.org_id = l.org_id and v.requirement_id = l.requirement_id and v.id = l.requirement_version_id
            join public.requirement r
              on r.org_id = l.org_id and r.project_id = l.project_id and r.id = l.requirement_id and r.deleted_at is null
           where l.org_id = @org_id and l.project_id = @project_id
             and (l.checklist_item_id in (select id from items) or l.manual_test_case_id in (select id from cases))
           order by l.id limit @max_links + 1
        ),
        reqs as (
          select r.id, r.kind, r.title, r.external_ref, r.feature_node_id, r.parent_requirement_id,
                 h.id as version_id, h.version_no, h.body, h.content_hash
            from public.requirement r
            join public.requirement_version h
              on h.org_id = r.org_id and h.requirement_id = r.id and h.version_no = r.head_version_no
           where r.org_id = @org_id and r.project_id = @project_id and r.deleted_at is null
             and (r.id in (select requirement_id from links) or r.feature_node_id in (select id from tree))
           order by r.id limit @max_rows + 1
        ),
        node_seed as (
          select id from tree
          union
          select feature_node_id from reqs where feature_node_id is not null
        )
        """ + Tail;

    /// <summary>
    /// The selected requirements at their head, the links that name them, the items and cases those links reach and the
    /// path above every feature node involved.
    /// </summary>
    internal const string ReadRequirements = """
        with recursive reqs as (
          select r.id, r.kind, r.title, r.external_ref, r.feature_node_id, r.parent_requirement_id,
                 h.id as version_id, h.version_no, h.body, h.content_hash
            from public.requirement r
            join public.requirement_version h
              on h.org_id = r.org_id and h.requirement_id = r.id and h.version_no = r.head_version_no
           where r.org_id = @org_id and r.project_id = @project_id and r.deleted_at is null and r.id = any(@ids)
           order by r.id limit @max_rows + 1
        ),
        links as (
          select l.id, l.requirement_id, l.requirement_version_id, v.version_no, l.checklist_item_id, l.manual_test_case_id
            from public.requirement_link l
            join public.requirement_version v
              on v.org_id = l.org_id and v.requirement_id = l.requirement_id and v.id = l.requirement_version_id
           where l.org_id = @org_id and l.project_id = @project_id
             and l.requirement_id in (select id from reqs)
           order by l.id limit @max_links + 1
        ),
        cases as (
          select c.id, c.checklist_item_id, c.code, c.title, c.description, c.precondition, c.steps, c.priority, c.state
            from public.manual_test_case c
           where c.org_id = @org_id and c.project_id = @project_id and c.deleted_at is null
             and (c.id in (select manual_test_case_id from links)
                  or c.checklist_item_id in (select checklist_item_id from links))
           order by c.id limit @max_rows + 1
        ),
        items as (
          select i.id, i.feature_node_id, i.title, i.description, i.priority, i.state
            from public.checklist_item i
           where i.org_id = @org_id and i.project_id = @project_id and i.deleted_at is null
             and (i.id in (select checklist_item_id from links)
                  or i.id in (select checklist_item_id from cases))
           order by i.id limit @max_rows + 1
        ),
        node_seed as (
          select feature_node_id as id from items
          union
          select feature_node_id from reqs where feature_node_id is not null
        )
        """ + Tail;

    /// <summary>
    /// The shared end of both reads: the path above the seed nodes, the latest authoring version of every case, and the
    /// one row of JSON arrays. Arrays are ordered by id, so the same data always reads the same.
    /// </summary>
    private const string Tail = """
        ,
        up as (
          select n.id, n.parent_id from public.feature_node n
           where n.org_id = @org_id and n.project_id = @project_id and n.id in (select id from node_seed)
          union
          select p.id, p.parent_id from public.feature_node p
            join up u on p.id = u.parent_id
           where p.org_id = @org_id and p.project_id = @project_id
        ),
        nodes as (
          select n.id, n.parent_id, n.kind, n.title, n.position
            from public.feature_node n
           where n.org_id = @org_id and n.project_id = @project_id and n.deleted_at is null
             and n.id in (select id from up)
           order by n.id limit @max_rows + 1
        ),
        pinned as (
          select c.*, av.version_no as pinned_version_no, av.content_hash as pinned_hash
            from cases c
            left join lateral (
              select v.version_no, v.content_hash
                from public.authoring_version v
               where v.org_id = @org_id and v.project_id = @project_id
                 and v.entity_kind = 'manual_test_case' and v.entity_id = c.id
               order by v.version_no desc limit 1
            ) av on true
        ),
        -- Uncompressed size of what the arrays below will carry, summed before any JSON is built (pg_column_size would
        -- count a compressed value at its stored size). Each row adds 160 bytes for its ids and keys.
        sizes as (
          select (select coalesce(sum(160 + octet_length(x.title)), 0) from nodes x)
               + (select coalesce(sum(160 + octet_length(x.title) + coalesce(octet_length(x.description), 0)), 0) from items x)
               + (select coalesce(sum(160 + octet_length(x.title) + coalesce(octet_length(x.description), 0)
                                      + coalesce(octet_length(x.precondition), 0) + octet_length(x.steps::text)), 0) from pinned x)
               + (select coalesce(sum(160 + octet_length(x.title) + octet_length(x.body)
                                      + coalesce(octet_length(x.external_ref), 0)), 0) from reqs x)
               + (select count(*) * 160 from links x) as bytes
        )
        select
          (select bytes from sizes)::bigint as bytes,
          case when (select bytes from sizes) > @max_bytes then '[]' else (select coalesce(jsonb_agg(jsonb_build_object(
              'id', n.id, 'parent_id', n.parent_id, 'kind', n.kind, 'title', n.title, 'position', n.position)
              order by n.id), '[]'::jsonb)::text from nodes n) end as nodes,
          case when (select bytes from sizes) > @max_bytes then '[]' else (select coalesce(jsonb_agg(jsonb_build_object(
              'id', i.id, 'feature_node_id', i.feature_node_id, 'title', i.title, 'description', i.description,
              'priority', i.priority, 'state', i.state) order by i.id), '[]'::jsonb)::text from items i) end as items,
          case when (select bytes from sizes) > @max_bytes then '[]' else (select coalesce(jsonb_agg(jsonb_build_object(
              'id', c.id, 'checklist_item_id', c.checklist_item_id, 'code', c.code, 'title', c.title,
              'description', c.description, 'precondition', c.precondition, 'steps', c.steps,
              'priority', c.priority, 'state', c.state, 'version_no', c.pinned_version_no,
              'content_hash', c.pinned_hash) order by c.id), '[]'::jsonb)::text from pinned c) end as cases,
          case when (select bytes from sizes) > @max_bytes then '[]' else (select coalesce(jsonb_agg(jsonb_build_object(
              'id', r.id, 'kind', r.kind, 'title', r.title, 'external_ref', r.external_ref,
              'feature_node_id', r.feature_node_id, 'parent_requirement_id', r.parent_requirement_id,
              'version_id', r.version_id, 'version_no', r.version_no, 'body', r.body,
              'content_hash', r.content_hash) order by r.id), '[]'::jsonb)::text from reqs r) end as requirements,
          case when (select bytes from sizes) > @max_bytes then '[]' else (select coalesce(jsonb_agg(jsonb_build_object(
              'id', l.id, 'requirement_id', l.requirement_id, 'requirement_version_id', l.requirement_version_id,
              'requirement_version_no', l.version_no, 'checklist_item_id', l.checklist_item_id,
              'manual_test_case_id', l.manual_test_case_id) order by l.id), '[]'::jsonb)::text from links l) end as links
        """;
}
