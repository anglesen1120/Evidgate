namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The two statements of the manual-case link routes and the one read of the catalog lookup they need. A link is
/// insert-or-delete and never updated, and both statements return the affected row's id so the caller can tell a first
/// write from a repeat without a second read: no id means nothing was written.
/// </summary>
internal static class AuthoringManualCaseLinkSql
{
    /// <summary>
    /// Inserts the link unless the case already holds it. `manual_case_link_uq` is the arbiter, so two concurrent
    /// requests for one pair collapse to a single row and the loser reads a null id — the 200 <c>changed:false</c>
    /// answer — instead of surfacing a 23505.
    /// </summary>
    internal const string InsertLink = """
        insert into public.manual_case_link
          (id, org_id, project_id, manual_test_case_id, test_case_id, linked_by_membership_id)
        values
          (@id, @org_id, @project_id, @manual_test_case_id, @test_case_id, @membership_id)
        on conflict on constraint manual_case_link_uq do nothing
        returning id
        """;

    /// <summary>
    /// Removes the link the route names. The catalog case id identifies it inside the manual case, so a caller never
    /// has to know the link row's own id; no returned id means the pair was not linked (a 404 for the route).
    /// </summary>
    internal const string DeleteLink = """
        delete from public.manual_case_link
         where org_id = @org_id
           and project_id = @project_id
           and manual_test_case_id = @manual_test_case_id
           and test_case_id = @test_case_id
        returning id
        """;
}
