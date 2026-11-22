using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class QuarantineSchemaTests
{
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
