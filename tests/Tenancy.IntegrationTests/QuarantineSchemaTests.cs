using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.3.1-T2 through a real PostgreSQL: migration 0030's <c>quarantine_entry</c> table (FORCE RLS,
/// the tenant key and the three composite FKs, the partial unique index, the app-role column grants
/// and the <c>evidgate.quarantine_entry_guard</c> state machine) and the <c>quarantine:write</c>
/// catalogue permission it seeds (07.3.1 §4/§6, D1/D3; TL T3, T23, T58).
/// <para>
/// The fixture is this project's own <see cref="PostgresTenancyFixture"/> — one digest-pinned
/// PostgreSQL 18 container per assembly and one database for the run (tests isolate by tenant), with a
/// private database only for the index-plan case. These tests pin the database contract (grants, guard,
/// RLS, indexes, seed), never internals; the HTTP surface is 07.3.1-T2.2's.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed class QuarantineSchemaTests
{
    private readonly PostgresTenancyFixture _fixture;

    public QuarantineSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid Org,
        Guid Project,
        Guid Case,
        Guid Owner,
        DedicatedDatabase? Owned = null) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();

            if (Owned is not null)
            {
                await Owned.DisposeAsync();
            }
        }
    }

    /// <summary>An org, a project, a catalog case and an active membership, on the shared database.</summary>
    /// <param name="dedicated">A private database for the index-plan case; the world drops it.</param>
    private async Task<World> StartAsync(DedicatedDatabase? dedicated = null)
    {
        var database = dedicated?.Name ?? await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var (caseId, owner) = await SeedCaseAndMemberAsync(database, org, project);

        return new World(api, client, database, org, project, caseId, owner, dedicated);
    }

    /// <summary>A catalog case and an active membership in the org, seeded as the owner (the migration role).</summary>
    private async Task<(Guid Case, Guid Owner)> SeedCaseAndMemberAsync(string database, Guid org, Guid project)
    {
        var caseId = Guid.CreateVersion7();
        var owner = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await scope.ExecuteAsync(
            "insert into public.membership (id, org_id, invited_email, status) values (@id, @org_id, @invited_email, 'active')",
            ("id", owner),
            ("org_id", org),
            ("invited_email", $"owner-{owner:N}@example.test"));
        await scope.ExecuteAsync(
            """
            insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
            values (@id, @org_id, @project_id, @annotation_id, 'probe', 'playwright', 'abcdef0')
            """,
            ("id", caseId),
            ("org_id", org),
            ("project_id", project),
            ("annotation_id", Guid.CreateVersion7()));
        await scope.CommitAsync();

        return (caseId, owner);
    }

    /// <summary>A second org with its own project, case and membership (for the cross-tenant FK cases).</summary>
    private async Task<(Guid Org, Guid Project, Guid Case, Guid Owner)> CreateOtherTenantAsync(World world)
    {
        var user = Guid.CreateVersion7();
        var slug = Unique.Slug("beta");
        var org = await CreateOrgAsync(world.Client, user, slug);
        var project = await CreateProjectAsync(world.Client, user, org, slug + "-project");
        var (caseId, owner) = await SeedCaseAndMemberAsync(world.Database, org, project);

        return (org, project, caseId, owner);
    }

    /// <summary>
    /// One raw insert of a quarantine entry as the app role: only the columns the app role may INSERT,
    /// so <c>state</c> takes its <c>'active'</c> default. Every tenant reference is overridable for the
    /// cross-tenant cases.
    /// </summary>
    private static string AppInsertSql(
        World world,
        Guid id,
        Guid? project = null,
        Guid? caseId = null,
        Guid? owner = null,
        string reason = "probe",
        DateTimeOffset? expiresAt = null) =>
        "insert into public.quarantine_entry (id, org_id, project_id, case_id, owner, reason, expires_at) "
        + $"values ('{id}', '{world.Org}', '{project ?? world.Project}', '{caseId ?? world.Case}', '{owner ?? world.Owner}', "
        + $"'{reason}', '{(expiresAt ?? DateTimeOffset.UtcNow.AddDays(7)).UtcDateTime:O}')";

    /// <summary>One committed active entry for <paramref name="world"/>'s case, inserted as the app role.</summary>
    private async Task InsertActiveAsync(World world, Guid id)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(AppInsertSql(world, id))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>One committed entry in a chosen terminal state, inserted as the owner (bypassing the API).</summary>
    private async Task InsertWithStateAsync(World world, Guid id, string state)
    {
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            "insert into public.quarantine_entry (id, org_id, project_id, case_id, owner, reason, state, expires_at) "
            + $"values ('{id}', '{world.Org}', '{world.Project}', '{world.Case}', '{world.Owner}', 'probe', '{state}', now() + interval '7 days')"))
            .ShouldBe(1);
        await owner.CommitAsync();
    }

    /// <summary>The columns <c>evidgate_app</c> may UPDATE on <paramref name="table"/>, in name order.</summary>
    private async Task<IReadOnlyList<string>> ColumnPrivilegesAsync(string database, string table, string privilege) =>
        await _fixture.ColumnAsync<string>(
            database,
            $"""
            select column_name
              from information_schema.columns
             where table_schema = 'public' and table_name = '{table}'
               and has_column_privilege('evidgate_app', 'public.{table}', column_name, '{privilege}')
             order by column_name
            """);

    // ------------------------------------------------------------------ (a) migration: RLS flags and the tenant key

    [Fact]
    public async Task QuarantineEntry_HasForcedRls_AndTheTenantKey()
    {
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.TableExistsAsync(database, "quarantine_entry")).ShouldBeTrue();

        var rls = await _fixture.RowSecurityAsync(database, "quarantine_entry");
        rls.Enabled.ShouldBeTrue();
        rls.Forced.ShouldBeTrue();

        (await _fixture.ConstraintsAsync(database, "quarantine_entry")).ShouldContain(
            "u:quarantine_entry_org_id_key:UNIQUE (org_id, id)",
            "quarantine_entry needs unique (org_id, id) as quarantine_entry_org_id_key (TL T3)");
    }

    // ------------------------------------------------------------------ (b) the three composite FKs

    [Fact]
    public async Task CompositeForeignKeys_Exist()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var constraints = await _fixture.ConstraintsAsync(database, "quarantine_entry");

        constraints.ShouldContain(
            constraint => constraint.StartsWith("f:quarantine_entry_project_fk:", StringComparison.Ordinal)
                && constraint.Contains("(org_id, project_id)", StringComparison.Ordinal)
                && constraint.Contains("project(org_id, id)", StringComparison.Ordinal),
            "quarantine_entry needs the composite project FK (org_id, project_id) -> project (org_id, id)");
        constraints.ShouldContain(
            constraint => constraint.StartsWith("f:quarantine_entry_case_fk:", StringComparison.Ordinal)
                && constraint.Contains("(org_id, case_id)", StringComparison.Ordinal)
                && constraint.Contains("test_case(org_id, id)", StringComparison.Ordinal),
            "quarantine_entry needs the composite case FK (org_id, case_id) -> test_case (org_id, id)");
        constraints.ShouldContain(
            constraint => constraint.StartsWith("f:quarantine_entry_owner_fk:", StringComparison.Ordinal)
                && constraint.Contains("(org_id, owner)", StringComparison.Ordinal)
                && constraint.Contains("membership(org_id, id)", StringComparison.Ordinal),
            "quarantine_entry needs the composite owner FK (org_id, owner) -> membership (org_id, id)");
    }

    [Fact]
    public async Task CrossTenantReferences_AreRejected()
    {
        await using var world = await StartAsync();
        var (_, otherProject, otherCase, otherOwner) = await CreateOtherTenantAsync(world);

        // The row is filed under world.Org, so the tenant policy passes and only the composite FK can reject it.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            var error = await scope.CaptureErrorAsync(AppInsertSql(world, Guid.CreateVersion7(), project: otherProject));
            error.SqlState.ShouldBe("23503");
            error.ConstraintName.ShouldBe("quarantine_entry_project_fk");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            var error = await scope.CaptureErrorAsync(AppInsertSql(world, Guid.CreateVersion7(), caseId: otherCase));
            error.SqlState.ShouldBe("23503");
            error.ConstraintName.ShouldBe("quarantine_entry_case_fk");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            var error = await scope.CaptureErrorAsync(AppInsertSql(world, Guid.CreateVersion7(), owner: otherOwner));
            error.SqlState.ShouldBe("23503");
            error.ConstraintName.ShouldBe("quarantine_entry_owner_fk");
        }
    }

    // ------------------------------------------------------------------ (c) the partial unique index

    [Fact]
    public async Task SecondActiveEntry_ForTheSameCase_FailsThePartialUniqueIndex()
    {
        await using var world = await StartAsync();
        var first = Guid.CreateVersion7();

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(AppInsertSql(world, first))).ShouldBe(1);
            await scope.CommitAsync();
        }

        // A second active entry for the same (org, project, case) is refused by the partial unique index.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.CaptureErrorAsync(AppInsertSql(world, Guid.CreateVersion7()))).SqlState.ShouldBe("23505");
        }

        // A removed entry does not block a new active one for the same case.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync($"update public.quarantine_entry set state = 'removed' where id = '{first}'")).ShouldBe(1);
            await scope.CommitAsync();
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(AppInsertSql(world, Guid.CreateVersion7()))).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.quarantine_entry where org_id = '{world.Org}'")).ShouldBe(2);
    }

    // ------------------------------------------------------------------ (d) RLS isolation

    [Fact]
    public async Task Select_WithoutTenantContext_ReturnsZero()
    {
        await using var world = await StartAsync();
        await InsertActiveAsync(world, Guid.CreateVersion7());

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database));

        (await scope.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue();
        (await scope.ScalarAsync<long>("select count(*) from public.quarantine_entry")).ShouldBe(0);
    }

    [Fact]
    public async Task Select_OtherOrg_ReturnsZero()
    {
        await using var world = await StartAsync();
        await InsertActiveAsync(world, Guid.CreateVersion7());

        await using var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(world.Database),
            Guid.CreateVersion7());

        (await scope.ScalarAsync<long>("select count(*) from public.quarantine_entry")).ShouldBe(0);
    }

    [Fact]
    public async Task Operator_WithTenantSet_ReadsZeroRows()
    {
        await using var world = await StartAsync();
        await InsertActiveAsync(world, Guid.CreateVersion7());

        // The operator holds the SELECT grant but no policy (TL T76, D1): with FORCE row level security
        // and `evidgate.org_id` set to the row's own org, the read is still zero.
        await using var scope = await TenantScope.BeginAsync(_fixture.OperatorConnectionString(world.Database), world.Org);

        (await scope.ScalarAsync<Guid>("select evidgate.current_org_id()")).ShouldBe(world.Org);
        (await scope.ScalarAsync<long>("select count(*) from public.quarantine_entry")).ShouldBe(0);
    }

    [Fact]
    public async Task TenantPolicy_IsScopedToTheAppRole()
    {
        var database = await _fixture.SharedDatabaseAsync();

        // A policy with no `to` clause applies to PUBLIC; the tenant policy must name evidgate_app only,
        // so the operator's SELECT grant reads zero rows (F1).
        (await _fixture.PoliciesAsync(database, "quarantine_entry")).ShouldBe(
        [
            "quarantine_entry_app_tenant:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
            "quarantine_entry_owner_backfill:evidgate_owner:*:true:true",
        ]);
    }

    // ------------------------------------------------------------------ (e) the app-role grants

    [Fact]
    public async Task AppRole_HasNoTableLevelInsertUpdateOrDelete_AndOnlyStateUpdate()
    {
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "quarantine_entry", "SELECT")).ShouldBeTrue();
        // A column-level GRANT is independent of the table-level one, so the table-level INSERT/UPDATE
        // stay false after the revoke (RlsTableCatalog:314).
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "quarantine_entry", "INSERT")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "quarantine_entry", "UPDATE")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "quarantine_entry", "DELETE")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "quarantine_entry", "TRUNCATE")).ShouldBeFalse();

        (await ColumnPrivilegesAsync(database, "quarantine_entry", "UPDATE")).ShouldBe(["state"]);
        (await ColumnPrivilegesAsync(database, "quarantine_entry", "INSERT")).ShouldBe(
            ["case_id", "expires_at", "id", "org_id", "owner", "project_id", "reason"]);
    }

    [Fact]
    public async Task AppRole_CannotDelete()
    {
        await using var world = await StartAsync();
        var id = Guid.CreateVersion7();
        await InsertActiveAsync(world, id);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.CaptureErrorAsync($"delete from public.quarantine_entry where id = '{id}'")).SqlState.ShouldBe("42501");

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.quarantine_entry where org_id = '{world.Org}'")).ShouldBe(1);
    }

    [Fact]
    public async Task AppRole_CannotUpdateColumnsOutsideState()
    {
        await using var world = await StartAsync();
        var id = Guid.CreateVersion7();
        await InsertActiveAsync(world, id);

        foreach (var assignment in new[]
        {
            "reason = 'changed'",
            "expires_at = now() + interval '1 day'",
            $"owner = '{Guid.CreateVersion7()}'",
            $"case_id = '{Guid.CreateVersion7()}'",
        })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
            (await scope.CaptureErrorAsync($"update public.quarantine_entry set {assignment} where id = '{id}'"))
                .SqlState.ShouldBe("42501");
        }
    }

    // ------------------------------------------------------------------ (f) the guard's state machine

    [Fact]
    public async Task AppRole_CanMoveActiveToRemoved()
    {
        await using var world = await StartAsync();
        var id = Guid.CreateVersion7();
        await InsertActiveAsync(world, id);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync($"update public.quarantine_entry set state = 'removed' where id = '{id}'")).ShouldBe(1);
        await scope.CommitAsync();

        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.quarantine_entry where id = '{id}'"))
            .ShouldBe("removed");
    }

    [Fact]
    public async Task AppRole_CannotMoveActiveToExpired()
    {
        await using var world = await StartAsync();
        var id = Guid.CreateVersion7();
        await InsertActiveAsync(world, id);

        // `expired` is written only by the 07.3.2-T2 sweeper, never by the app role.
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync($"update public.quarantine_entry set state = 'expired' where id = '{id}'");

        error.SqlState.ShouldBe("23514");
        error.MessageText.ShouldContain("quarantine_entry");
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.quarantine_entry where id = '{id}'"))
            .ShouldBe("active");
    }

    [Fact]
    public async Task TerminalStates_CannotReturnToActive()
    {
        await using var world = await StartAsync();
        var removed = Guid.CreateVersion7();
        var expired = Guid.CreateVersion7();
        await InsertWithStateAsync(world, removed, "removed");
        await InsertWithStateAsync(world, expired, "expired");

        foreach (var id in new[] { removed, expired })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
            var error = await scope.CaptureErrorAsync($"update public.quarantine_entry set state = 'active' where id = '{id}'");

            error.SqlState.ShouldBe("23514");
            error.MessageText.ShouldContain("quarantine_entry");
        }
    }

    [Fact]
    public async Task OwnerRole_CanMoveActiveToExpired()
    {
        await using var world = await StartAsync();
        var id = Guid.CreateVersion7();
        await InsertActiveAsync(world, id);

        // The 07.3.2-T2 sweeper runs as an owner-run function, so `current_user` is evidgate_owner here.
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync($"update public.quarantine_entry set state = 'expired' where id = '{id}'")).ShouldBe(1);
        await owner.CommitAsync();

        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.quarantine_entry where id = '{id}'"))
            .ShouldBe("expired");
    }

    [Fact]
    public async Task OwnerRole_CannotDelete()
    {
        await using var world = await StartAsync();
        var id = Guid.CreateVersion7();
        await InsertActiveAsync(world, id);

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        var error = await owner.CaptureErrorAsync($"delete from public.quarantine_entry where id = '{id}'");

        error.SqlState.ShouldBe("23514");
        error.MessageText.ShouldContain("quarantine_entry");
    }

    // ------------------------------------------------------------------ (g) P4: the list and sweeper indexes

    [Fact]
    public async Task ListAndDueQueries_UseTheirIndexes()
    {
        // Dedicated: it seeds 4100 rows, ANALYZEs and disables sequential scans for its own plan.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(dedicated);

        // 2100 cases in the target project: 100 due (past expiry) and 2000 future, one active entry each
        // (the partial unique index allows one active entry per case); 2000 removed entries give the list
        // keyset volume. ANALYZE then lets each index be chosen on its own; sequential scans are disabled
        // so a small table cannot make the planner prefer one.
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
            select gen_random_uuid(), '{world.Org}', '{world.Project}', gen_random_uuid(), 'due-' || g, 'playwright', 'abcdef0'
              from generate_series(1, 2100) g;
            insert into public.quarantine_entry (id, org_id, project_id, case_id, owner, reason, state, expires_at, created_at)
            select gen_random_uuid(), '{world.Org}', '{world.Project}', c.id, '{world.Owner}', 'due', 'active',
                   case when c.g <= 100 then now() - interval '1 hour' else now() + interval '7 days' end,
                   now() - c.g * interval '1 minute'
              from (select id, row_number() over (order by id) as g
                      from public.test_case
                     where org_id = '{world.Org}' and project_id = '{world.Project}' and name like 'due-%') c;
            insert into public.quarantine_entry (id, org_id, project_id, case_id, owner, reason, state, expires_at, created_at)
            select gen_random_uuid(), '{world.Org}', '{world.Project}', '{world.Case}', '{world.Owner}', 'removed', 'removed',
                   now() + interval '7 days', now() - g * interval '1 second'
              from generate_series(1, 2000) g;
            analyze public.quarantine_entry;
            """);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        await scope.SetAsync("enable_seqscan", "off");

        // The list's first page: one project, keyset (created_at, id) ascending.
        var list = await scope.ScalarAsync<string>(
            $"""
            explain (format json)
            select id from public.quarantine_entry
             where org_id = '{world.Org}' and project_id = '{world.Project}'
             order by created_at, id
             limit 51
            """);
        list.ShouldContain("quarantine_entry_project_created_idx");

        // The expiry sweeper's per-project due rows (07.3.2-T2): state and expires_at inside one project.
        var due = await scope.ScalarAsync<string>(
            $"""
            explain (format json)
            select id from public.quarantine_entry
             where org_id = '{world.Org}' and project_id = '{world.Project}'
               and state = 'active' and expires_at <= now()
            """);
        due.ShouldContain("quarantine_entry_state_expiry_idx");
    }

    // ------------------------------------------------------------------ (h) the permission seed (D1)

    [Fact]
    public async Task PermissionSeed_GrantsQuarantineWriteToTestMaintainerOnly()
    {
        var database = await _fixture.SharedDatabaseAsync();

        // The permission row exists with the D1 fields, exactly as contracts/permissions.json declares them.
        (await _fixture.ScalarAsync<string>(
            database,
            "select description from public.permission where code = 'quarantine:write'"))
            .ShouldBe("Create or remove a quarantine entry; never for a case on the active policy's critical list (T23, 07.3.1).");

        (await _fixture.ColumnAsync<string>(
            database,
            """
            select resource || ':' || action || ':' || sensitive::text || ':' || reserved::text || ':' || tenant_assignable::text
              from public.permission
             where code = 'quarantine:write'
            """)).ShouldBe(["quarantine:write:false:false:true"]);

        // Granted to the built-in role test_maintainer and to NO other built-in role.
        (await _fixture.ColumnAsync<string>(
            database,
            """
            select r.code
              from public.role_permission rp
              join public.role r on r.id = rp.role_id
             where rp.permission_code = 'quarantine:write'
             order by r.code
            """)).ShouldBe(["test_maintainer"]);
    }
}
