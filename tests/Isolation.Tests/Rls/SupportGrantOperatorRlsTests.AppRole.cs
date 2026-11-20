using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// The <c>support_grant</c> guard and shape checks as the application role the handlers use
/// (<c>evidgate_app</c> with <c>evidgate.org_id</c> set), plus the owner path where RLS does not narrow the
/// project lookup. Every refusal asserts the SQLSTATE and the message fragment or constraint name, and the
/// same fact runs the nearest legal statement as a control so the refusal is the guard's alone.
/// </summary>
public sealed partial class SupportGrantOperatorRlsTests
{
    private Task<TenantScope> AppAsync(string database, Guid org) =>
        TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);

    [Theory]
    [InlineData("approver_rewritten", "Approved", "update public.support_grant set approved_by = '{other}' where id = @g", "approval is write-once",
        "update public.support_grant set approved_by = approved_by where id = @g")]
    [InlineData("approval_cleared", "Approved", "update public.support_grant set approved_at = null, approved_by = null where id = @g", "approval is write-once",
        "update public.support_grant set revoked_at = now() where id = @g")]
    [InlineData("revoked_unrevoked", "Revoked", "update public.support_grant set revoked_at = null where id = @g", "a revoked grant accepts no change",
        "update public.support_grant set revoked_at = now() where id = @g")]
    [InlineData("revoked_reapproved", "Revoked", "update public.support_grant set approved_at = now(), approved_by = '{approver}', revoked_at = null where id = @g", "a revoked grant accepts no change",
        "update public.support_grant set approved_at = now(), approved_by = '{approver}' where id = @g")]
    [InlineData("approve_and_revoke_together", "Requested", "update public.support_grant set approved_at = now(), approved_by = '{approver}', revoked_at = now() where id = @g", "cannot be approved and revoked in one change",
        "update public.support_grant set approved_at = now(), approved_by = '{approver}' where id = @g")]
    public async Task AppRole_Guard_Refuses_Transition(string name, string from, string template, string refusal, string control)
    {
        var database = await WorldAsync();
        var grant = await GrantAsync(database, OrgA, Operator, Enum.Parse<State>(from));
        var sql = Fill(template);

        await using (var scope = await AppAsync(database, OrgA))
        {
            var error = await scope.CaptureErrorAsync(sql, ("g", grant));
            error.SqlState.ShouldBe("23514", name);
            error.MessageText.ShouldContain(refusal, customMessage: name);
        }

        // The nearest legal statement on a fresh grant of the same state passes, so the refusal is the guard's.
        var twin = await GrantAsync(database, OrgA, Operator, Enum.Parse<State>(from == "Revoked" ? "Requested" : from));
        await using var allowed = await AppAsync(database, OrgA);
        (await allowed.ExecuteAsync(Fill(control), ("g", twin))).ShouldBe(1, name);
    }

    private static string Fill(string template) =>
        template.Replace("{other}", OtherOperator.ToString()).Replace("{approver}", Approver.ToString());

    [Fact]
    public async Task AppRole_Guard_Refuses_Approving_An_Expired_Request_That_A_Fresh_One_Passes()
    {
        var database = await WorldAsync();
        var expired = await ExpiredRequestedAsync(database);
        var fresh = await GrantAsync(database, OrgA, Operator, State.Requested);
        const string approve = "update public.support_grant set approved_at = now(), approved_by = @a where id = @g";

        await using (var refused = await AppAsync(database, OrgA))
        {
            var error = await refused.CaptureErrorAsync(approve, ("a", Approver), ("g", expired));
            error.SqlState.ShouldBe("23514");
            error.MessageText.ShouldContain("has expired and cannot be approved");
        }

        await using var allowed = await AppAsync(database, OrgA);
        (await allowed.ExecuteAsync(approve, ("a", Approver), ("g", fresh))).ShouldBe(1);
    }

    [Fact]
    public async Task AppRole_Guard_Allows_Approve_Then_Revoke_With_Server_Decision_Times()
    {
        var database = await WorldAsync();
        var grant = await GrantAsync(database, OrgA, Operator, State.Requested);

        await using var scope = await AppAsync(database, OrgA);
        (await scope.ScalarAsync<bool>("""
            with u as (update public.support_grant set approved_at = now() + interval '5 years', approved_by = @a
                        where id = @g returning approved_at)
            select approved_at = statement_timestamp() from u
            """, ("a", Approver), ("g", grant))).ShouldBeTrue("the approval time is the server's");
        (await scope.ScalarAsync<bool>("""
            with u as (update public.support_grant set revoked_at = now() - interval '1 hour'
                        where id = @g returning revoked_at)
            select revoked_at = statement_timestamp() from u
            """, ("g", grant))).ShouldBeTrue("the revocation time is the server's");
    }

    [Theory]
    [InlineData("scopes_2d_two_rows", "'{{case:read},{case:write}}'", "'{}'", "support_grant_scopes_check", "'{case:read,case:write}'", "'{}'")]
    [InlineData("scopes_2d_one_row", "'{{case:read,case:write}}'", "'{}'", "support_grant_scopes_check", "'{case:read,case:write}'", "'{}'")]
    [InlineData("project_ids_2d", "'{case:read}'", "'{{P1},{P2}}'", "support_grant_project_ids_check", "'{case:read}'", "'{P1,P2}'")]
    public async Task AppRole_Insert_Refuses_A_MultiDimensional_Array(
        string name, string scopes, string projects, string constraint, string twinScopes, string twinProjects)
    {
        var database = await WorldAsync();
        string Paths(string list) => list.Replace("P1", ProjectA1.ToString()).Replace("P2", ProjectA2.ToString());
        const string insert = """
            insert into public.support_grant (org_id, operator_user_id, scopes, project_ids, reason, expires_at)
            values (@o, @u, {0}, {1}, 'r', now() + interval '1 hour')
            """;

        await using (var scope = await AppAsync(database, OrgA))
        {
            var error = await scope.CaptureErrorAsync(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, insert, scopes, Paths(projects)),
                ("o", OrgA), ("u", Operator));
            error.SqlState.ShouldBe("23514", name);
            error.ConstraintName.ShouldBe(constraint, name);
        }

        // Only the dimensionality differs in the twin, so the CHECK is what refused the array.
        await using var allowed = await AppAsync(database, OrgA);
        (await allowed.ExecuteAsync(
            string.Format(System.Globalization.CultureInfo.InvariantCulture, insert, twinScopes, Paths(twinProjects)),
            ("o", OrgA), ("u", Operator))).ShouldBe(1, name);
    }

    [Fact]
    public async Task AppRole_ProjectScope_Refuses_Foreign_Unknown_And_Mixed_Lists_But_Accepts_Duplicates()
    {
        var database = await WorldAsync();
        var lists = new[]
        {
            ("foreign", $"{{{ProjectB1}}}"),
            ("unknown", $"{{{Guid.CreateVersion7()}}}"),
            ("mixed", $"{{{ProjectA1},{ProjectB1}}}"),
        };
        foreach (var (name, list) in lists)
        {
            await using var refused = await AppAsync(database, OrgA);
            var error = await refused.CaptureErrorAsync(InsertWithProjects, ("o", OrgA), ("u", Operator), ("p", list));
            error.SqlState.ShouldBe("23503", name);
            error.MessageText.ShouldContain("lists a project that is not in the organization", customMessage: name);
        }

        await using var allowed = await AppAsync(database, OrgA);
        (await allowed.ExecuteAsync(InsertWithProjects, ("o", OrgA), ("u", Operator), ("p", $"{{{ProjectA1},{ProjectA1}}}")))
            .ShouldBe(1, "a repeated project of the org is one project, not a mismatch");
    }

    [Fact]
    public async Task OwnerPath_ProjectScope_Refuses_A_Project_Of_Another_Org_Where_Rls_Does_Not_Narrow()
    {
        var database = await WorldAsync();
        await using (var probe = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), OrgA))
        {
            (await probe.ScalarAsync<int>("select count(*)::int from public.project where id = @p", ("p", ProjectB1)))
                .ShouldBe(1, "the owner policy shows the foreign project, so only the guard's org predicate can refuse it");
        }

        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), OrgA);
        var error = await scope.CaptureErrorAsync(InsertWithProjects, ("o", OrgA), ("u", Operator), ("p", $"{{{ProjectB1}}}"));
        error.SqlState.ShouldBe("23503");
        error.MessageText.ShouldContain("lists a project that is not in the organization");

        await using var allowed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), OrgA);
        (await allowed.ExecuteAsync(InsertWithProjects, ("o", OrgA), ("u", Operator), ("p", $"{{{ProjectA1}}}"))).ShouldBe(1);
    }

    [Fact]
    public async Task AppRole_Insert_For_Another_Org_Is_Refused_By_The_Tenant_Policy()
    {
        var database = await WorldAsync();
        await using (var scope = await AppAsync(database, OrgA))
        {
            var error = await scope.CaptureErrorAsync(InsertWithProjects, ("o", OrgB), ("u", Operator), ("p", "{}"));
            error.SqlState.ShouldBe("42501");
            error.MessageText.ShouldContain("new row violates row-level security policy for table \"support_grant\"");
        }

        await using var own = await AppAsync(database, OrgB);
        (await own.ExecuteAsync(InsertWithProjects, ("o", OrgB), ("u", Operator), ("p", "{}"))).ShouldBe(1);
    }

    private const string InsertWithProjects = """
        insert into public.support_grant (org_id, operator_user_id, scopes, project_ids, reason, expires_at)
        values (@o, @u, '{case:read}', @p::uuid[], 'r', now() + interval '1 hour')
        """;
}
