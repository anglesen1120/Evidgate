using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>The transition guard and shape checks of <c>support_grant</c>, exercised on the superuser connection.</summary>
public sealed partial class SupportGrantOperatorRlsTests
{
    [Theory]
    [InlineData("approved_to_requested", "update public.support_grant set approved_at = null, approved_by = null where id = '{approved}'", "approval is write-once")]
    [InlineData("approver_rewritten", "update public.support_grant set approved_by = '{operator}' where id = '{approved}'", "approval is write-once")]
    [InlineData("approved_again", "update public.support_grant set approved_at = now() where id = '{approved}'", "approval is write-once")]
    [InlineData("revoked_to_approved", "update public.support_grant set approved_at = now(), approved_by = '{approver}', revoked_at = null where id = '{revoked}'", "a revoked grant accepts no change")]
    [InlineData("revoked_unrevoked", "update public.support_grant set revoked_at = null where id = '{revoked}'", "a revoked grant accepts no change")]
    [InlineData("revoked_edited", "update public.support_grant set reason = 'again' where id = '{revoked}'", "identity, scope and expiry are immutable")]
    [InlineData("expired_requested_approved", "update public.support_grant set approved_at = now(), approved_by = '{approver}' where id = '{expiredRequested}'", "has expired and cannot be approved")]
    [InlineData("scopes_changed", "update public.support_grant set scopes = '{case:*}' where id = '{requested}'", "identity, scope and expiry are immutable")]
    [InlineData("projects_changed", "update public.support_grant set project_ids = '{projectA1}' where id = '{requested}'", "identity, scope and expiry are immutable")]
    [InlineData("expiry_extended", "update public.support_grant set expires_at = expires_at + interval '1 day' where id = '{requested}'", "identity, scope and expiry are immutable")]
    [InlineData("operator_changed", "update public.support_grant set operator_user_id = '{other}' where id = '{requested}'", "identity, scope and expiry are immutable")]
    [InlineData("org_changed", "update public.support_grant set org_id = '{orgB}' where id = '{requested}'", "identity, scope and expiry are immutable")]
    [InlineData("approve_and_revoke_together", "update public.support_grant set approved_at = now(), approved_by = '{approver}', revoked_at = now() where id = '{requested}'", "cannot be approved and revoked in one change")]
    [InlineData("revoked_noop", "update public.support_grant set revoked_at = revoked_at where id = '{revoked}'", "a revoked grant accepts no change")]
    [InlineData("delete", "delete from public.support_grant where id = '{requested}'", "is never deleted")]
    public async Task Guard_Refuses_Transition(string name, string template, string refusal)
    {
        var database = await WorldAsync();
        var requested = await GrantAsync(database, OrgA, Operator, State.Requested);
        var approved = await GrantAsync(database, OrgA, Operator, State.Approved);
        var revoked = await GrantAsync(database, OrgA, Operator, State.Revoked);
        var expiredRequested = await ExpiredRequestedAsync(database);

        var sql = template
            .Replace("{requested}", requested.ToString())
            .Replace("{approved}", approved.ToString())
            .Replace("{revoked}", revoked.ToString())
            .Replace("{expiredRequested}", expiredRequested.ToString())
            .Replace("{approver}", Approver.ToString())
            .Replace("{operator}", Operator.ToString())
            .Replace("{other}", OtherOperator.ToString())
            .Replace("{orgB}", OrgB.ToString())
            .Replace("{projectA1}", "{" + ProjectA1 + "}");

        var error = await Should.ThrowAsync<PostgresException>(() => _fixture.ExecuteAsync(database, sql), name);
        error.SqlState.ShouldBe("23514", name);
        error.MessageText.ShouldContain(refusal, customMessage: name);
    }

    [Fact]
    public async Task Guard_Refuses_ApproverEqualToRequester()
    {
        var database = await WorldAsync();
        var requested = await GrantAsync(database, OrgA, Operator, State.Requested);
        var error = await Should.ThrowAsync<PostgresException>(() => _fixture.ExecuteAsync(
            database,
            $"update public.support_grant set approved_at = now(), approved_by = '{Operator}' where id = '{requested}'"));
        error.SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task Guard_Refuses_InsertOfAnAlreadyApprovedGrant()
    {
        var database = await WorldAsync();
        var error = await Should.ThrowAsync<PostgresException>(() => _fixture.ExecuteAsync(database, $"""
            insert into public.support_grant (org_id, operator_user_id, scopes, reason, approved_by, approved_at, expires_at)
            values ('{OrgA}', '{Operator}', '{CaseRead}', 'r', '{Approver}', now(), now() + interval '1 hour')
            """));
        error.SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task Guard_Allows_Approve_ThenRevoke()
    {
        var database = await WorldAsync();
        var requested = await GrantAsync(database, OrgA, Operator, State.Requested, projects: [ProjectA1]);

        await _fixture.ExecuteAsync(database,
            $"update public.support_grant set approved_at = now(), approved_by = '{Approver}' where id = '{requested}'");
        (await CountsAsync(database, requested, Operator)).ShouldBe([1, 1, 1, 1], "an approved grant reads");

        await _fixture.ExecuteAsync(database, $"update public.support_grant set revoked_at = now() where id = '{requested}'");
        (await CountsAsync(database, requested, Operator)).ShouldBe([0, 0, 0, 0], "a revoked grant reads nothing");
    }

    [Theory]
    [InlineData("scopes", "'{}'")]
    [InlineData("scopes", "'{Case:read}'")]
    [InlineData("scopes", "'{read}'")]
    [InlineData("scopes", "'{\"case:read,case:write\"}'")]
    [InlineData("scopes", "'{NULL,case:read}'")]
    [InlineData("scopes", "'{NULL,\"case:read,case:write\"}'")]
    [InlineData("reason", "''")]
    [InlineData("expires_at", "now() - interval '3 hours'")]
    [InlineData("expires_at", "now() + interval '72 hours 1 second'")]
    public async Task Shape_Checks_Refuse_Bad_Input(string column, string value)
    {
        var database = await WorldAsync();
        var scopes = column == "scopes" ? value : "'{case:read}'";
        var reason = column == "reason" ? value : "'r'";
        var expires = column == "expires_at" ? value : "now() + interval '1 hour'";

        var error = await Should.ThrowAsync<PostgresException>(() => _fixture.ExecuteAsync(database, $"""
            insert into public.support_grant (org_id, operator_user_id, scopes, reason, expires_at)
            values ('{OrgA}', '{Operator}', {scopes}, {reason}, {expires})
            """));
        error.SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task Expiry_Window_Accepts_Exactly_72_Hours_After_The_Server_Set_Creation_Time()
    {
        var database = await WorldAsync();
        await _fixture.ExecuteAsync(database, $"""
            insert into public.support_grant (org_id, operator_user_id, scopes, reason, expires_at)
            values ('{OrgA}', '{Operator}', '{CaseRead}', 'edge', now() + interval '72 hours')
            """);
    }

    [Fact]
    public async Task Guard_Allows_Withdrawing_A_Requested_Grant()
    {
        var database = await WorldAsync();
        var requested = await GrantAsync(database, OrgA, Operator, State.Requested);
        (await _fixture.ScalarAsync<bool>(database, $"""
            with u as (update public.support_grant set revoked_at = now() where id = '{requested}'
                       returning revoked_at, approved_at, approved_by)
            select revoked_at is not null and approved_at is null and approved_by is null from u
            """)).ShouldBeTrue("the withdrawal is stored as a revocation of a never-approved grant");
        (await CountsAsync(database, requested, Operator)).ShouldBe([0, 0, 0, 0]);
    }

    [Fact]
    public async Task Guard_Replaces_Caller_Chosen_Decision_Times_With_The_Server_Time()
    {
        var database = await WorldAsync();
        var requested = await GrantAsync(database, OrgA, Operator, State.Requested);
        (await _fixture.ScalarAsync<bool>(database, $"""
            with u as (update public.support_grant
                          set approved_at = now() + interval '5 years', approved_by = '{Approver}'
                        where id = '{requested}' returning approved_at)
            select approved_at = statement_timestamp() from u
            """)).ShouldBeTrue("the approval time is the server's statement time, not the caller's");

        (await _fixture.ScalarAsync<bool>(database, $"""
            with u as (update public.support_grant set revoked_at = now() - interval '1 minute'
                        where id = '{requested}' returning revoked_at)
            select revoked_at = statement_timestamp() from u
            """)).ShouldBeTrue("the revocation time is the server's statement time, not the caller's");
    }

    [Fact]
    public async Task Guard_Refuses_A_Project_Of_Another_Org_And_An_Unknown_Project()
    {
        var database = await WorldAsync();
        foreach (var project in new[] { ProjectB1, Guid.CreateVersion7() })
        {
            var list = "{" + project + "}";
            var error = await Should.ThrowAsync<PostgresException>(() => _fixture.ExecuteAsync(database, $"""
                insert into public.support_grant (org_id, operator_user_id, scopes, project_ids, reason, expires_at)
                values ('{OrgA}', '{Operator}', '{CaseRead}', '{list}', 'r', now() + interval '1 hour')
                """));
            error.SqlState.ShouldBe("23503");
        }
    }

    private async Task<Guid> ExpiredRequestedAsync(string database)
    {
        var id = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(database, $"""
            set session_replication_role = replica;
            insert into public.support_grant (id, org_id, operator_user_id, scopes, reason, expires_at, created_at)
            values ('{id}', '{OrgA}', '{Operator}', '{CaseRead}', 'late', now() - interval '10 minutes', now() - interval '2 hours');
            """);
        return id;
    }
}
