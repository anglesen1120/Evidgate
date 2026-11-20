using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// The support grant and the operator read policies of its first table set (<c>support_grant</c>,
/// <c>organization</c>, <c>project</c>, <c>test_case</c>), asserted as the audited-read role by direct SQL.
/// The table is unreachable from HTTP in this slice, so these facts are the only thing that makes each rule fire:
/// every refusal is asserted as zero rows or a server error, and a positive control proves the fixture can read.
/// </summary>
[Collection(IsolationSuite.Name)]
[Trait("Category", "Docker")]
public sealed class SupportGrantOperatorRlsTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-1111111111a1");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-7222-8222-2222222222b2");
    private static readonly Guid ProjectA1 = Guid.Parse("11111111-1111-7111-8111-1111111111c1");
    private static readonly Guid ProjectA2 = Guid.Parse("11111111-1111-7111-8111-1111111111c2");
    private static readonly Guid ProjectB1 = Guid.Parse("22222222-2222-7222-8222-2222222222c1");
    private static readonly Guid Operator = Guid.Parse("33333333-3333-7333-8333-333333333301");
    private static readonly Guid OtherOperator = Guid.Parse("33333333-3333-7333-8333-333333333302");
    private static readonly Guid Approver = Guid.Parse("33333333-3333-7333-8333-333333333303");

    private const string CaseRead = "{case:read}";

    private static readonly object Gate = new();
    private static Task<string>? _world;

    private readonly PostgresIsolationFixture _fixture;

    public SupportGrantOperatorRlsTests(PostgresIsolationFixture fixture) => _fixture = fixture;

    /// <summary>One migrated, seeded database for every fact: grants are added per fact under fresh ids.</summary>
    private Task<string> WorldAsync()
    {
        lock (Gate)
        {
            return _world ??= SeedAsync();
        }
    }

    private async Task<string> SeedAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await _fixture.ExecuteAsync(database, $"""
            insert into public.organization (id, name, slug) values
              ('{OrgA}', 'A', 'sg-org-a'), ('{OrgB}', 'B', 'sg-org-b');
            insert into public."user" (id, email) values
              ('{Operator}', 'op@sg.test'), ('{OtherOperator}', 'op2@sg.test'), ('{Approver}', 'admin@sg.test');
            insert into public.project (id, org_id, name, slug, case_prefix) values
              ('{ProjectA1}', '{OrgA}', 'A1', 'a1', 'AA'),
              ('{ProjectA2}', '{OrgA}', 'A2', 'a2', 'AB'),
              ('{ProjectB1}', '{OrgB}', 'B1', 'b1', 'BA');
            insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit) values
              (uuidv7(), '{OrgA}', '{ProjectA1}', 'case-a1', 'a1', 'pytest', 'aaaaaaa'),
              (uuidv7(), '{OrgA}', '{ProjectA2}', 'case-a2', 'a2', 'pytest', 'aaaaaaa'),
              (uuidv7(), '{OrgB}', '{ProjectB1}', 'case-b1', 'b1', 'pytest', 'aaaaaaa');
            """);
        return database;
    }

    private enum State { Requested, Approved, Expired, Revoked }

    /// <summary>
    /// Files a grant on the superuser connection. Past timestamps are seeded with the user triggers off,
    /// because the guard (rightly) refuses approving an expired grant.
    /// </summary>
    private async Task<Guid> GrantAsync(
        string database,
        Guid org,
        Guid operatorId,
        State state,
        string scopes = "{case:read}",
        Guid[]? projects = null)
    {
        var id = Guid.CreateVersion7();
        var projectList = projects is null ? "{}" : "{" + string.Join(",", projects) + "}";
        var approver = state == State.Requested ? "null" : $"'{Approver}'";
        var approvedAt = state switch
        {
            State.Requested => "null",
            State.Expired => "now() - interval '90 minutes'",
            _ => "now() - interval '1 minute'",
        };
        var expiresAt = state == State.Expired ? "now() - interval '30 minutes'" : "now() + interval '1 hour'";
        var revokedAt = state == State.Revoked ? "now() - interval '30 seconds'" : "null";

        await _fixture.ExecuteAsync(database, $"""
            set session_replication_role = replica;
            insert into public.support_grant
              (id, org_id, operator_user_id, scopes, project_ids, reason, approved_by, approved_at, expires_at, revoked_at, created_at)
            values
              ('{id}', '{org}', '{operatorId}', '{scopes}', '{projectList}', 'investigate', {approver}, {approvedAt},
               {expiresAt}, {revokedAt}, now() - interval '2 hours');
            """);
        return id;
    }

    private static async Task<TenantScope> OperatorAsync(PostgresIsolationFixture fixture, string database, Guid? grant, Guid? user)
    {
        var scope = await TenantScope.BeginAsync(fixture.OperatorConnectionString(database));
        if (grant is { } g)
        {
            await scope.SetAsync("evidgate.support_grant_id", g.ToString("D"));
        }

        if (user is { } u)
        {
            await scope.SetAsync("evidgate.operator_user_id", u.ToString("D"));
        }

        return scope;
    }

    private async Task<int[]> CountsAsync(string database, Guid? grant, Guid? user)
    {
        await using var scope = await OperatorAsync(_fixture, database, grant, user);
        return
        [
            await scope.ScalarAsync<int>("select count(*)::int from public.test_case"),
            await scope.ScalarAsync<int>("select count(*)::int from public.project"),
            await scope.ScalarAsync<int>("select count(*)::int from public.organization"),
            await scope.ScalarAsync<int>("select count(*)::int from public.support_grant"),
        ];
    }

    [Fact]
    public async Task OperatorRls_NoGrant_ZeroRows()
    {
        var database = await WorldAsync();
        (await CountsAsync(database, null, null)).ShouldBe([0, 0, 0, 0]);
        (await CountsAsync(database, Guid.CreateVersion7(), Operator)).ShouldBe([0, 0, 0, 0], "an unknown grant id");

        // A malformed setting is "no grant", never a server error that would tell the two apart.
        await using var scope = await TenantScope.BeginAsync(_fixture.OperatorConnectionString(database));
        await scope.SetAsync("evidgate.support_grant_id", "not-a-uuid");
        await scope.SetAsync("evidgate.operator_user_id", Operator.ToString("D"));
        (await scope.ScalarAsync<int>("select count(*)::int from public.test_case")).ShouldBe(0);
    }

    [Fact]
    public async Task OperatorRls_ValidGrant_ReadsOnlyItsOrgAndProject()
    {
        var database = await WorldAsync();
        var grant = await GrantAsync(database, OrgA, Operator, State.Approved, projects: [ProjectA1]);

        // test_case: project A1 only; project: A1 only; organization: A; support_grant: the grant itself.
        (await CountsAsync(database, grant, Operator)).ShouldBe([1, 1, 1, 1]);
    }

    [Fact]
    public async Task OperatorRls_OrgScopedGrantWithoutProjects_ReadsOrgTablesOnly()
    {
        var database = await WorldAsync();
        var grant = await GrantAsync(database, OrgA, Operator, State.Approved);

        // No project listed: org-scoped tables answer, project-keyed tables stay empty (fail closed).
        (await CountsAsync(database, grant, Operator)).ShouldBe([0, 0, 1, 1]);
    }

    [Fact]
    public async Task OperatorRls_WildcardScope_Reads()
    {
        var database = await WorldAsync();
        var grant = await GrantAsync(database, OrgA, Operator, State.Approved, "{case:*}", [ProjectA1, ProjectA2]);
        (await CountsAsync(database, grant, Operator)).ShouldBe([2, 2, 1, 1]);
    }

    [Fact]
    public async Task OperatorRls_WriteOnlyScope_ZeroRows()
    {
        var database = await WorldAsync();
        var grant = await GrantAsync(database, OrgA, Operator, State.Approved, "{case:write}", [ProjectA1]);
        (await CountsAsync(database, grant, Operator)).ShouldBe([0, 0, 0, 0]);
    }

    [Fact]
    public async Task OperatorRls_ExpiredGrant_ZeroRows()
    {
        var database = await WorldAsync();
        var grant = await GrantAsync(database, OrgA, Operator, State.Expired, projects: [ProjectA1]);
        (await CountsAsync(database, grant, Operator)).ShouldBe([0, 0, 0, 0]);
    }

    [Fact]
    public async Task OperatorRls_RevokedGrant_ZeroRows()
    {
        var database = await WorldAsync();
        var grant = await GrantAsync(database, OrgA, Operator, State.Revoked, projects: [ProjectA1]);
        (await CountsAsync(database, grant, Operator)).ShouldBe([0, 0, 0, 0]);
    }

    [Fact]
    public async Task OperatorRls_RequestedGrant_ZeroRows()
    {
        var database = await WorldAsync();
        var grant = await GrantAsync(database, OrgA, Operator, State.Requested, projects: [ProjectA1]);
        (await CountsAsync(database, grant, Operator)).ShouldBe([0, 0, 0, 0]);
    }

    [Fact]
    public async Task OperatorRls_GrantOfAnotherOperator_ZeroRows()
    {
        var database = await WorldAsync();
        var grant = await GrantAsync(database, OrgA, OtherOperator, State.Approved, projects: [ProjectA1]);
        (await CountsAsync(database, grant, Operator)).ShouldBe([0, 0, 0, 0]);
        (await CountsAsync(database, grant, OtherOperator)).ShouldBe([1, 1, 1, 1], "the owner of the grant still reads");
    }

    [Fact]
    public async Task OperatorRls_GrantOfAnotherOrg_NeverReadsThisOrg()
    {
        var database = await WorldAsync();
        var grant = await GrantAsync(database, OrgB, Operator, State.Approved, projects: [ProjectB1]);

        await using var scope = await OperatorAsync(_fixture, database, grant, Operator);
        (await scope.ScalarAsync<int>("select count(*)::int from public.test_case where org_id = @o", ("o", OrgA))).ShouldBe(0);
        (await scope.ScalarAsync<int>("select count(*)::int from public.project where org_id = @o", ("o", OrgA))).ShouldBe(0);
        (await scope.ScalarAsync<int>("select count(*)::int from public.organization where id = @o", ("o", OrgA))).ShouldBe(0);
        (await scope.ScalarAsync<int>("select count(*)::int from public.test_case where org_id = @o", ("o", OrgB))).ShouldBe(1);
    }

    [Fact]
    public async Task OperatorRls_GrantListingAnotherProject_ZeroRowsOfThisProject()
    {
        var database = await WorldAsync();
        var grant = await GrantAsync(database, OrgA, Operator, State.Approved, projects: [ProjectA2]);

        await using var scope = await OperatorAsync(_fixture, database, grant, Operator);
        (await scope.ScalarAsync<int>("select count(*)::int from public.test_case where project_id = @p", ("p", ProjectA1))).ShouldBe(0);
        (await scope.ScalarAsync<int>("select count(*)::int from public.project where id = @p", ("p", ProjectA1))).ShouldBe(0);
        (await scope.ScalarAsync<int>("select count(*)::int from public.test_case where project_id = @p", ("p", ProjectA2))).ShouldBe(1);
    }

    [Fact]
    public async Task OperatorRls_WriteOutsideScope_Denied()
    {
        var database = await WorldAsync();
        var grant = await GrantAsync(database, OrgA, Operator, State.Approved, "{case:*}", [ProjectA1]);

        foreach (var sql in new[]
        {
            "update public.test_case set name = 'x'",
            "delete from public.test_case",
            "insert into public.project (id, org_id, name, slug, case_prefix) values (uuidv7(), '" + OrgA + "', 'n', 'n', 'ZZ')",
            "update public.support_grant set reason = 'x'",
            "delete from public.support_grant",
        })
        {
            await using var scope = await OperatorAsync(_fixture, database, grant, Operator);
            var error = await scope.CaptureErrorAsync(sql);
            error.SqlState.ShouldBe("42501", sql);
        }
    }

    [Fact]
    public async Task OperatorRls_ForgedSettingWithoutRow_StillZeroRows()
    {
        var database = await WorldAsync();
        var grant = await GrantAsync(database, OrgA, Operator, State.Requested, projects: [ProjectA1]);

        // Approval cannot be claimed through the setting: only the stored row counts.
        await using var scope = await OperatorAsync(_fixture, database, grant, Operator);
        await scope.SetAsync("evidgate.org_id", OrgA.ToString("D"));
        (await scope.ScalarAsync<int>("select count(*)::int from public.test_case")).ShouldBe(0);
    }

    [Theory]
    [InlineData("approved_to_requested", "update public.support_grant set approved_at = null, approved_by = null where id = '{approved}'")]
    [InlineData("approver_rewritten", "update public.support_grant set approved_by = '{operator}' where id = '{approved}'")]
    [InlineData("approved_again", "update public.support_grant set approved_at = now() where id = '{approved}'")]
    [InlineData("revoked_to_approved", "update public.support_grant set approved_at = now(), approved_by = '{approver}', revoked_at = null where id = '{revoked}'")]
    [InlineData("revoked_unrevoked", "update public.support_grant set revoked_at = null where id = '{revoked}'")]
    [InlineData("revoked_edited", "update public.support_grant set reason = 'again' where id = '{revoked}'")]
    [InlineData("expired_requested_approved", "update public.support_grant set approved_at = now(), approved_by = '{approver}' where id = '{expiredRequested}'")]
    [InlineData("scopes_changed", "update public.support_grant set scopes = '{case:*}' where id = '{requested}'")]
    [InlineData("projects_changed", "update public.support_grant set project_ids = '{}' where id = '{requested}'")]
    [InlineData("expiry_extended", "update public.support_grant set expires_at = expires_at + interval '1 day' where id = '{requested}'")]
    [InlineData("operator_changed", "update public.support_grant set operator_user_id = '{other}' where id = '{requested}'")]
    [InlineData("org_changed", "update public.support_grant set org_id = '{orgB}' where id = '{requested}'")]
    [InlineData("approve_and_revoke_together", "update public.support_grant set approved_at = now(), approved_by = '{approver}', revoked_at = now() where id = '{requested}'")]
    [InlineData("delete", "delete from public.support_grant where id = '{requested}'")]
    public async Task Guard_Refuses_Transition(string name, string template)
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
            .Replace("{orgB}", OrgB.ToString());

        var error = await Should.ThrowAsync<PostgresException>(() => _fixture.ExecuteAsync(database, sql), name);
        error.SqlState.ShouldBe("23514", name);
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
    [InlineData("reason", "''")]
    [InlineData("expires_at", "now() - interval '3 hours'")]
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
    public async Task Definers_Are_Hardened_And_Callable_Only_By_The_Operator()
    {
        var database = await WorldAsync();
        var rows = await _fixture.ColumnAsync<string>(database, """
            select p.proname || '|' || p.prosecdef || '|' || p.provolatile || '|' || coalesce(p.proconfig::text, '')
                   || '|' || (select count(*) from aclexplode(p.proacl) a where a.grantee = 0)
                   || '|' || has_function_privilege('evidgate_operator', p.oid, 'execute')
                   || '|' || has_function_privilege('evidgate_app', p.oid, 'execute')
                   || '|' || has_function_privilege('evidgate_worker', p.oid, 'execute')
                   || '|' || has_function_privilege('evidgate_platform_operator', p.oid, 'execute')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate'
               and p.proname in ('operator_grant_id', 'operator_grant_org', 'operator_grant_ok', 'resolve_active_grant')
             order by p.proname
            """);

        rows.ShouldBe(
        [
            "operator_grant_id|True|s|{search_path=\"\"}|0|True|False|False|False",
            "operator_grant_ok|True|s|{search_path=\"\"}|0|True|False|False|False",
            "operator_grant_org|True|s|{search_path=\"\"}|0|True|False|False|False",
            "resolve_active_grant|True|s|{search_path=\"\"}|0|True|False|False|False",
        ]);
    }

    [Fact]
    public async Task ResolveActiveGrant_Returns_Only_The_Callers_Valid_Grants()
    {
        var database = await WorldAsync();
        var org = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(database, $"insert into public.organization (id, name, slug) values ('{org}', 'R', 'sg-r-{org.ToString("N")[..8]}')");
        var valid = await GrantAsync(database, org, Operator, State.Approved, projects: [ProjectA1]);
        await GrantAsync(database, org, Operator, State.Expired);
        await GrantAsync(database, org, Operator, State.Revoked);
        await GrantAsync(database, org, Operator, State.Requested);
        await GrantAsync(database, org, OtherOperator, State.Approved);

        await using var scope = await TenantScope.BeginAsync(_fixture.OperatorConnectionString(database));
        var ids = await scope.ScalarAsync<string>(
            "select string_agg(grant_id::text, ',') from evidgate.resolve_active_grant(@u, @o)", ("u", Operator), ("o", org));
        ids.ShouldBe(valid.ToString());
        (await scope.ScalarAsync<int>(
            "select count(*)::int from evidgate.resolve_active_grant(@u, @o)", ("u", Operator), ("o", Guid.CreateVersion7()))).ShouldBe(0);
    }

    [Fact]
    public async Task App_Role_Is_Tenant_Scoped_On_SupportGrant_And_Cannot_Call_The_Definers()
    {
        var database = await WorldAsync();
        var a = await GrantAsync(database, OrgA, Operator, State.Approved);
        var b = await GrantAsync(database, OrgB, Operator, State.Approved);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), OrgA))
        {
            (await scope.ScalarAsync<int>("select count(*)::int from public.support_grant where id = @i", ("i", a))).ShouldBe(1);
            (await scope.ScalarAsync<int>("select count(*)::int from public.support_grant where id = @i", ("i", b))).ShouldBe(0);
        }

        // A refused statement aborts its transaction, so each refusal gets its own scope.
        foreach (var sql in new[] { "delete from public.support_grant", "select evidgate.operator_grant_org()", "select evidgate.operator_grant_ok(null, null, 'read')" })
        {
            await using var refused = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), OrgA);
            (await refused.CaptureErrorAsync(sql)).SqlState.ShouldBe("42501", sql);
        }

        // The approval path the next task builds: the app role may set exactly the three decision columns.
        await using var approve = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), OrgA);
        var requested = Guid.CreateVersion7();
        await approve.ExecuteAsync(
            "insert into public.support_grant (id, org_id, operator_user_id, scopes, reason, expires_at) values (@i, @o, @u, '{case:read}', 'r', now() + interval '1 hour')",
            ("i", requested), ("o", OrgA), ("u", Operator));
        (await approve.ExecuteAsync(
            "update public.support_grant set approved_at = now(), approved_by = @a where id = @i", ("a", Approver), ("i", requested))).ShouldBe(1);
        await approve.CommitAsync();

        await using var rewrite = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), OrgA);
        (await rewrite.CaptureErrorAsync("update public.support_grant set reason = 'x' where id = @i", ("i", requested))).SqlState.ShouldBe("42501");
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
