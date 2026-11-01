namespace Evidgate.Modules.Catalog.Infrastructure.Persistence;

/// <summary>
/// The two statements an evaluation reads from the catalogue, kept here because SQL text belongs to Infrastructure.
/// Both pass the caller's trusted <c>org_id</c> explicitly as well as relying on row level security, so a caller that
/// asks for another tenant's rows gets none even where the policy is wider than the app role.
/// </summary>
internal static class EvaluationReadSql
{
    /// <summary>
    /// One suite revision's evaluation facts: the slug of its suite and whether the revision declares no member. The
    /// <c>not exists</c> probe answers the second fact without a second statement or a count over up to ten thousand
    /// members, and both tables are joined on their composite tenant key as well as the id.
    /// </summary>
    internal const string LoadSuite = """
        select s.slug as "SuiteKey",
               not exists (
                 select 1
                   from public.suite_member sm
                  where sm.org_id = @org_id
                    and sm.suite_revision_id = sr.id) as "DeclaredEmpty"
          from public.suite_revision sr
          join public.suite s on s.org_id = sr.org_id and s.id = sr.suite_id
         where sr.org_id = @org_id
           and sr.id = @suite_revision_id
        """;

    /// <summary>
    /// The case and its display name behind each named test revision. The id batch travels as one JSON text because the
    /// tenant Dapper path refuses a client-side array parameter; the manifest caps a plan at ten thousand executions, so
    /// the batch stays bounded.
    /// </summary>
    internal const string LoadCases = """
        select r.id as "CaseRevisionId",
               c.id as "CaseId",
               c.name as "CaseKey"
          from public.test_revision r
          join public.test_case c on c.org_id = r.org_id and c.id = r.case_id
         where r.org_id = @org_id
           and r.id in (select value::uuid from jsonb_array_elements_text(@ids::jsonb))
        """;
}
