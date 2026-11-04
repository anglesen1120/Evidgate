using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The retained history of abandoned proposals against a real PostgreSQL: only <c>discarded</c> rows
/// past the 180-day window are removable, the sweep is the one role allowed to remove them, and a
/// backlog drains in bounded batches rather than in one long transaction.
/// <para>
/// The function is called the way the schedule calls it — as <c>evidgate_worker_sched</c>, on a
/// superuser session that has delegated its identity with <c>set role</c>, so the EXECUTE grant and
/// the <c>SECURITY DEFINER</c> owner path are both exercised rather than assumed.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed class AuthoringChangePurgeTests
{
    /// <summary>The retention window the plan ships as its default (<c>[INFERENCE] D9</c>).</summary>
    private const int RetentionDays = 180;

    private readonly PostgresTenancyFixture _fixture;

    public AuthoringChangePurgeTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Purge_DeletesOnlyExpiredDiscardedChanges()
    {
        // The sweep is tenant-free: it deletes every expired discard in the database, so a shared
        // database would let one fact (or a sibling suite) delete another's fixtures and break the
        // exact counts below. A private database at head is the fixture's seam for unfiltered counts.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        var tenant = await SeedTenantAsync(database);

        var expiredDiscard = await SeedChangeAsync(database, tenant, "discarded", RetentionDays + 1, RetentionDays + 1);
        var freshDiscard = await SeedChangeAsync(database, tenant, "discarded", RetentionDays + 1, 1);
        var applied = await SeedChangeAsync(database, tenant, "applied", RetentionDays + 1, 0);
        var pending = await SeedChangeAsync(database, tenant, "pending", RetentionDays + 1, 0);

        var removed = await PurgeAsWorkerAsync(database, limit: 100);

        removed.ShouldBe(1, "exactly the one discarded row past the window is eligible");
        (await ExistingAsync(database, expiredDiscard)).ShouldBeFalse("the expired discard is purged");

        // The three survivors pin each branch of the predicate: a fresh discard is still provable, an
        // applied change is review evidence of the version it carries, and a pending one is live work.
        (await ExistingAsync(database, freshDiscard)).ShouldBeTrue("a discard inside the window is kept");
        (await ExistingAsync(database, applied)).ShouldBeTrue("an applied change is never purged");
        (await ExistingAsync(database, pending)).ShouldBeTrue("a pending change is never purged");
    }

    [Fact]
    public async Task Purge_DrainsBacklogInBoundedBatches()
    {
        // The sweep is tenant-free: it deletes every expired discard in the database, so a shared
        // database would let one fact (or a sibling suite) delete another's fixtures and break the
        // exact counts below. A private database at head is the fixture's seam for unfiltered counts.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        var tenant = await SeedTenantAsync(database);

        for (var i = 0; i < 3; i++)
        {
            await SeedChangeAsync(database, tenant, "discarded", RetentionDays + 1, RetentionDays + 1);
        }

        (await PurgeAsWorkerAsync(database, limit: 2)).ShouldBe(2, "the batch size bounds one call");
        (await PurgeAsWorkerAsync(database, limit: 2)).ShouldBe(1, "the next call drains the remainder");
        (await PurgeAsWorkerAsync(database, limit: 2)).ShouldBe(0, "a drained table deletes nothing");

        // A non-positive limit matches the idempotency sweep's `least(greatest(p_limit, 0), 10000)`:
        // it deletes nothing instead of raising or meaning "unbounded".
        (await PurgeAsWorkerAsync(database, limit: 0)).ShouldBe(0);
        (await PurgeAsWorkerAsync(database, limit: -5)).ShouldBe(0);
    }

    [Fact]
    public async Task Purge_IsAnOwnerDefinerAndOnlyTheSweepMayExecute()
    {
        // The sweep is tenant-free: it deletes every expired discard in the database, so a shared
        // database would let one fact (or a sibling suite) delete another's fixtures and break the
        // exact counts below. A private database at head is the fixture's seam for unfiltered counts.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        var tenant = await SeedTenantAsync(database);
        var expired = await SeedChangeAsync(database, tenant, "discarded", RetentionDays + 1, RetentionDays + 1);

        var config = await FunctionConfigAsync(database);
        config.Definer.ShouldBeTrue("the sweep runs as its owner, which the FORCE RLS backfill policy allows");
        config.Owner.ShouldBe("evidgate_owner");
        // PostgreSQL renders `set search_path = ''` in proconfig in its canonical form, quotes included
        // (postgres:18.6), so the element is compared as stored rather than as the migration spells it.
        config.Config.ShouldContain(
            "search_path=\"\"",
            "a definer function must not inherit the caller's search_path");

        (await _fixture.ScalarAsync<bool>(database, ExecutePrivilegeSql("evidgate_worker_sched")))
            .ShouldBeTrue("the schedule is the caller the grant names");
        (await _fixture.ScalarAsync<bool>(database, ExecutePrivilegeSql("evidgate_app")))
            .ShouldBeFalse("an app session must not be able to delete another tenant's history");
        (await _fixture.ScalarAsync<bool>(database, ExecutePrivilegeSql("evidgate_operator")))
            .ShouldBeFalse();

        var appCall = await CaptureAppErrorAsync(database, tenant.Org);
        appCall.SqlState.ShouldBe("42501", "the ACL refuses the app before the function body runs");

        (await ExistingAsync(database, expired)).ShouldBeTrue("a refused call deletes nothing");
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "authoring_change", "DELETE"))
            .ShouldBeFalse("the app soft-deletes nothing here: only the sweep removes a row");
    }

    // ------------------------------------------------------------------ helpers

    private const string InsertChangeSql =
        "insert into public.authoring_change "
        + "(id, org_id, project_id, target_id, change_type, payload, base_version, affected_versions, "
        + "state, author_membership_id, applied_at, applied_batch_id, applied_by_membership_id, "
        + "discarded_at, discarded_by_membership_id, created_at, updated_at) "
        + "values (@id, @org_id, @project_id, @target_id, 'comment', '{\"body\":\"probe\"}'::jsonb, 1, "
        + "@affected::jsonb, @state, @author, @applied_at, @applied_batch, @applied_by, "
        + "@discarded_at, @discarded_by, @created_at, @updated_at)";

    private static string ExecutePrivilegeSql(string role) =>
        $"select has_function_privilege('{role}', 'evidgate.purge_expired_authoring_changes(integer)', 'EXECUTE')";

    /// <summary>An org, a project, one node, one item and one author membership, written as the owner.</summary>
    private async Task<Tenant> SeedTenantAsync(string database)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var node = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();
        var author = Guid.CreateVersion7();

        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await seed.ExecuteAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            ("id", org),
            ("name", Unique.Slug("acme")),
            ("slug", Unique.Slug("acme")));
        await seed.ExecuteAsync(
            "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
            ("id", project),
            ("org_id", org),
            ("name", Unique.Slug("checkout")),
            ("slug", Unique.Slug("checkout")));
        await seed.ExecuteAsync(
            "insert into public.membership (id, org_id, invited_email) values (@id, @org_id, @email)",
            ("id", author),
            ("org_id", org),
            ("email", "author-" + org.ToString("N")[..8] + "@example.test"));
        await seed.ExecuteAsync(
            "insert into public.feature_node (id, org_id, project_id, kind, title, position) "
            + "values (@id, @org_id, @project_id, 'module', 'Shop', 0)",
            ("id", node),
            ("org_id", org),
            ("project_id", project));
        await seed.ExecuteAsync(
            "insert into public.checklist_item "
            + "(id, org_id, project_id, feature_node_id, title, priority, state, source, position) "
            + "values (@id, @org_id, @project_id, @node, 'Pay by card', 'high', 'active', 'manual', 0)",
            ("id", item),
            ("org_id", org),
            ("project_id", project),
            ("node", node));
        await seed.CommitAsync();

        return new Tenant(org, project, item, author);
    }

    /// <summary>
    /// One proposal in a chosen state. <paramref name="ageDays"/> backdates <c>created_at</c>;
    /// <paramref name="outcomeAgeDays"/> backdates the terminal stamp (discard or apply).
    /// </summary>
    private async Task<Guid> SeedChangeAsync(
        string database,
        Tenant tenant,
        string state,
        int ageDays,
        int outcomeAgeDays)
    {
        var id = Guid.CreateVersion7();
        var batch = Guid.CreateVersion7();
        object applied = state == "applied" ? DateTime.UtcNow.AddDays(-outcomeAgeDays) : DBNull.Value;
        object appliedBatch = state == "applied" ? batch : DBNull.Value;
        object appliedBy = state == "applied" ? tenant.Author : DBNull.Value;
        object discarded = state == "discarded" ? DateTime.UtcNow.AddDays(-outcomeAgeDays) : DBNull.Value;
        object discardedBy = state == "discarded" ? tenant.Author : DBNull.Value;

        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await seed.ExecuteAsync(
            InsertChangeSql,
            ("id", id),
            ("org_id", tenant.Org),
            ("project_id", tenant.Project),
            ("target_id", tenant.Item),
            ("affected", "{\"" + tenant.Item.ToString("D") + "\":1}"),
            ("state", state),
            ("author", tenant.Author),
            ("applied_at", applied),
            ("applied_batch", appliedBatch),
            ("applied_by", appliedBy),
            ("discarded_at", discarded),
            ("discarded_by", discardedBy),
            ("created_at", DateTime.UtcNow.AddDays(-ageDays)),
            ("updated_at", DateTime.UtcNow.AddDays(-ageDays)));
        await seed.CommitAsync();

        return id;
    }

    /// <summary>Calls the sweep as the schedule does: delegate to <c>evidgate_worker_sched</c>.</summary>
    private async Task<int> PurgeAsWorkerAsync(string database, int limit)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database);
        await using var command = connection.CreateCommand();
        command.CommandText = "set role evidgate_worker_sched;";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        await using var call = connection.CreateCommand();
        call.CommandText = "select evidgate.purge_expired_authoring_changes($1)";
        call.Parameters.Add(new NpgsqlParameter { Value = limit });
        var removed = await call.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        return Convert.ToInt32(removed, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<PostgresException> CaptureAppErrorAsync(string database, Guid org)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);

        return await scope.CaptureErrorAsync("select evidgate.purge_expired_authoring_changes(10)");
    }

    private Task<bool> ExistingAsync(string database, Guid id) =>
        _fixture.ScalarAsync<bool>(
            database,
            $"select exists (select 1 from public.authoring_change where id = '{id}')");

    private async Task<(bool Definer, string Owner, string[] Config)> FunctionConfigAsync(string database)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "select prosecdef, proowner::regrole::text, proconfig "
            + "from pg_proc where oid = 'evidgate.purge_expired_authoring_changes(integer)'::regprocedure";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue("the function exists");

        return (
            reader.GetBoolean(0),
            reader.GetString(1),
            await reader.IsDBNullAsync(2, TestContext.Current.CancellationToken)
                ? []
                : (string[])reader.GetValue(2));
    }

    private sealed record Tenant(Guid Org, Guid Project, Guid Item, Guid Author);
}
