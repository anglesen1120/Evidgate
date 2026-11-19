namespace Evidgate.Modules.Catalog.Infrastructure.Persistence;

/// <summary>
/// The Dapper statements of the suite execution plan (00 §5.5 C5): the revision's project for the project gate, then one
/// keyset statement per page. Every table carries the tenant predicate itself, so the page never depends on the plan
/// the planner would otherwise pick for its RLS qual.
/// </summary>
internal static class SuitePlanReadSql
{
    /// <summary>The project of a revision's suite; the project gate reads it before any member is touched.</summary>
    internal const string RevisionProject = """
        select s.project_id
          from public.suite_revision r
          join public.suite s on s.org_id = r.org_id and s.id = r.suite_id
         where r.org_id = @org_id
           and r.id = @revision_id
        """;

    private const string PageColumns = """
               m.case_revision_id, tr.case_id, v.variant_hash, m.ordinal, m.id
        """;

    private const string PageFrom = """
          from public.suite_member m
          join public.test_revision tr on tr.org_id = m.org_id and tr.id = m.case_revision_id
          join public.variant v on v.org_id = m.org_id and v.id = m.variant_id
        """;

    /// <summary>The first page: the revision's members in <c>(ordinal, id)</c> order, one extra row to decide the next page.</summary>
    internal const string FirstPage = $"""
        select {PageColumns}
          {PageFrom}
         where m.org_id = @org_id
           and m.suite_revision_id = @revision_id
         order by m.ordinal,
                  m.id
         limit @limit
        """;

    /// <summary>
    /// The next page. <c>(ordinal, id)'s</c> strict order is the index <c>suite_member_plan_idx</c>'s order, so a row
    /// written between two pages sorts after the cursor and is neither repeated nor skipped.
    /// </summary>
    internal const string NextPage = $"""
        select {PageColumns}
          {PageFrom}
         where m.org_id = @org_id
           and m.suite_revision_id = @revision_id
           and (m.ordinal, m.id) > (@cursor_ordinal::integer, @cursor_id::uuid)
         order by m.ordinal,
                  m.id
         limit @limit
        """;
}
