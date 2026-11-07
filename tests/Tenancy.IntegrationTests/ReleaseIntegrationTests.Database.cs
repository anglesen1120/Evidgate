// Direct database coverage of the three release tables: grants, triggers, RLS, composite foreign keys and uniques.
// The release rows are made through the API; every refusal is then attempted as the role that would attempt it.
#pragma warning disable CA1861
using System.Net;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseIntegrationTests
{
    [Fact]
    public async Task ReleaseSchema_HasForcedRowSecurityAndTheApiGrants()
    {
        var database = await _fixture.SharedDatabaseAsync();

        foreach (var table in new[] { "release", "release_component", "release_run" })
        {
            (await _fixture.TableExistsAsync(database, table)).ShouldBeTrue($"{table} exists");
            var security = await _fixture.RowSecurityAsync(database, table);
            security.Enabled.ShouldBeTrue($"{table} has row security");
            security.Forced.ShouldBeTrue($"{table} forces row security on its owner");
            (await _fixture.PoliciesAsync(database, table))
                .ShouldContain(policy => policy.Contains(":evidgate_app:", StringComparison.Ordinal), $"{table} scopes evidgate_app");
        }

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "release_component", "SELECT")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "release_component", "INSERT")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "release_component", "UPDATE")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "release_component", "DELETE")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "release_run", "UPDATE")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "release_run", "DELETE")).ShouldBeFalse();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.release', 'state', 'UPDATE')")).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_column_privilege('evidgate_app', 'public.release', 'name', 'UPDATE')")).ShouldBeFalse();
    }

    [Fact]
    public async Task ReleaseRunSchema_HasCompositeForeignKeysUniquesAndIndex()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var constraints = await _fixture.ConstraintsAsync(database, "release_run");

        constraints.ShouldContain(entry => entry.StartsWith("f:release_run_release_fk:", StringComparison.Ordinal)
            && entry.Contains("FOREIGN KEY (org_id, release_id)", StringComparison.Ordinal));
        constraints.ShouldContain(entry => entry.StartsWith("f:release_run_run_fk:", StringComparison.Ordinal)
            && entry.Contains("FOREIGN KEY (org_id, run_id)", StringComparison.Ordinal));
        constraints.ShouldContain(entry => entry == "u:release_run_binding_key:UNIQUE (org_id, release_id, run_id, component_key)");
        constraints.ShouldContain(entry => entry == "u:release_run_org_id_key:UNIQUE (org_id, id)");

        (await _fixture.IndexesAsync(database, "release_run"))
            .ShouldContain(
                index => index.StartsWith("release_run_run_created_idx:", StringComparison.Ordinal)
                    && index.Contains("(org_id, run_id, created_at, id)", StringComparison.Ordinal),
                "the run's bindings are read through (org_id, run_id, created_at, id)");
    }

    [Fact]
    public async Task ReleaseComponent_AppRoleIsInsertSelectOnly_AndRefusesUpdateAndDelete()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await CreateReleaseAsync(world, "release-component-grants", state: null, OneComponent(world));
        var componentId = await ComponentIdAsync(world, releaseId, "web");

        var update = await AppErrorAsync(
            world,
            $"update public.release_component set digest = '{Digest('b')}' where id = '{componentId:D}'");
        update.SqlState.ShouldBe("42501");

        var delete = await AppErrorAsync(world, $"delete from public.release_component where id = '{componentId:D}'");
        delete.SqlState.ShouldBe("42501");

        (await ComponentCountAsync(world, releaseId)).ShouldBe(1);
        (await ComponentDigestAsync(world, componentId)).ShouldBe(Digest('a'));
    }

    [Fact]
    public async Task ReleaseComponent_TriggerRefusesUpdateAndDeleteForEveryRole()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await CreateReleaseAsync(world, "release-component-trigger", state: null, OneComponent(world));
        var componentId = await ComponentIdAsync(world, releaseId, "web");

        var update = await OwnerErrorAsync(
            world,
            $"update public.release_component set digest = '{Digest('b')}' where id = '{componentId:D}'");
        update.SqlState.ShouldBeOneOf("23514", "P0001");

        var delete = await OwnerErrorAsync(world, $"delete from public.release_component where id = '{componentId:D}'");
        delete.SqlState.ShouldBeOneOf("23514", "P0001");

        (await ComponentCountAsync(world, releaseId)).ShouldBe(1);
        (await ComponentDigestAsync(world, componentId)).ShouldBe(Digest('a'));
    }

    [Fact]
    public async Task ReleaseComponent_InsertIntoActiveParentIsRefused_DraftAccepted()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var active = await CreateReleaseAsync(world, "release-active-parent", "active", OneComponent(world));
        var draft = await CreateReleaseAsync(world, "release-draft-parent", state: null, OneComponent(world));

        var appRefusal = await AppErrorAsync(world, InsertComponentSql(world, active, "api", Digest('b')));
        appRefusal.SqlState.ShouldBeOneOf("23514", "P0001");
        var ownerRefusal = await OwnerErrorAsync(world, InsertComponentSql(world, active, "api", Digest('c')));
        ownerRefusal.SqlState.ShouldBeOneOf("23514", "P0001");
        (await ComponentCountAsync(world, active)).ShouldBe(1);

        (await AppExecuteAsync(world, InsertComponentSql(world, draft, "api", Digest('d')))).ShouldBe(1);
        (await ComponentCountAsync(world, draft)).ShouldBe(2);
    }

    [Fact]
    public async Task Release_IdentityColumnsAreImmutable_ForOwnerAndApp()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await CreateReleaseAsync(world, "release-identity", state: null, OneComponent(world));

        var ownerName = await OwnerErrorAsync(world, $"update public.release set name = 'hijacked' where id = '{releaseId:D}'");
        ownerName.SqlState.ShouldBeOneOf("23514", "P0001");
        var ownerProject = await OwnerErrorAsync(
            world,
            $"update public.release set project_id = '{Guid.CreateVersion7():D}' where id = '{releaseId:D}'");
        ownerProject.SqlState.ShouldBeOneOf("23514", "P0001");
        var appName = await AppErrorAsync(world, $"update public.release set name = 'hijacked' where id = '{releaseId:D}'");
        appName.SqlState.ShouldBe("42501");

        (await ReleaseScalarAsync(world, releaseId, "name")).ShouldBe("release-identity");
        (await ReleaseScalarAsync(world, releaseId, "project_id")).ShouldBe($"{world.Project:D}");
        (await ReleaseScalarAsync(world, releaseId, "version")).ShouldBe("1");
    }

    [Fact]
    public async Task Release_StateTransition_OnlyDraftToActive()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await CreateReleaseAsync(world, "release-state-machine", state: null, OneComponent(world));

        (await AppExecuteAsync(world, $"update public.release set state = 'active' where id = '{releaseId:D}'")).ShouldBe(1);
        (await ReleaseScalarAsync(world, releaseId, "state")).ShouldBe("active");
        (await ReleaseScalarAsync(world, releaseId, "version")).ShouldBe("2");

        var ownerBack = await OwnerErrorAsync(world, $"update public.release set state = 'draft' where id = '{releaseId:D}'");
        ownerBack.SqlState.ShouldBeOneOf("23514", "P0001");
        var appBack = await AppErrorAsync(world, $"update public.release set state = 'draft' where id = '{releaseId:D}'");
        appBack.SqlState.ShouldBeOneOf("23514", "P0001", "42501");

        (await ReleaseScalarAsync(world, releaseId, "state")).ShouldBe("active");
        (await ReleaseScalarAsync(world, releaseId, "version")).ShouldBe("2");
    }

    [Fact]
    public async Task ReleaseRun_ForceRls_ScopesReadsToTheTenant()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var worldA = await RunCreationTests.StartAsync(_fixture, database: dedicated, slug: "alpha");
        await using var worldB = await RunCreationTests.StartAsync(_fixture, database: dedicated, slug: "beta");
        var releaseA = await CreateReleaseAsync(worldA, "release-rls-alpha", "active", OneComponent(worldA));
        var releaseB = await CreateReleaseAsync(worldB, "release-rls-beta", "active", OneComponent(worldB));
        var runA = await CancelRunTestData.CreateQueuedRunAsync(worldA);
        var runB = await CancelRunTestData.CreateQueuedRunAsync(worldB);
        (await OwnerExecuteAsync(worldA, InsertRunSql(worldA, releaseA, runA, "web"))).ShouldBe(1);
        (await OwnerExecuteAsync(worldB, InsertRunSql(worldB, releaseB, runB, "web"))).ShouldBe(1);

        await using (var unscoped = await TenantScope.BeginAsync(_fixture.AppConnectionString(dedicated)))
        {
            (await unscoped.ScalarAsync<long>("select count(*) from public.release_run")).ShouldBe(0);
        }

        await using (var scopedA = await TenantScope.BeginAsync(_fixture.AppConnectionString(dedicated), worldA.Org, worldA.Project))
        {
            (await scopedA.ScalarAsync<long>("select count(*) from public.release_run")).ShouldBe(1);
            (await scopedA.ScalarAsync<Guid>("select release_id from public.release_run")).ShouldBe(releaseA);
        }

        await using (var scopedB = await TenantScope.BeginAsync(_fixture.AppConnectionString(dedicated), worldB.Org, worldB.Project))
        {
            (await scopedB.ScalarAsync<long>("select count(*) from public.release_run")).ShouldBe(1);
            (await scopedB.ScalarAsync<Guid>("select release_id from public.release_run")).ShouldBe(releaseB);
        }

        (await _fixture.ScalarAsync<long>(dedicated, "select count(*) from public.release_run")).ShouldBe(2);
    }

    [Fact]
    public async Task ReleaseRun_RejectsCrossOrgParents_WithSameOrgPositiveControl()
    {
        await using var worldA = await RunCreationTests.StartAsync(_fixture, slug: "alpha");
        await using var worldB = await RunCreationTests.StartAsync(_fixture, slug: "beta");
        var releaseA = await CreateReleaseAsync(worldA, "release-fk-alpha", "active", OneComponent(worldA));
        var releaseB = await CreateReleaseAsync(worldB, "release-fk-beta", "active", OneComponent(worldB));
        var runA = await CancelRunTestData.CreateQueuedRunAsync(worldA);
        var runB = await CancelRunTestData.CreateQueuedRunAsync(worldB);

        (await OwnerExecuteAsync(worldA, InsertRunSql(worldA, releaseA, runA, "web"))).ShouldBe(1);

        var foreignRelease = await OwnerErrorAsync(worldA, InsertRunSql(worldA, releaseB, runA, "api"));
        foreignRelease.SqlState.ShouldBe("23503", foreignRelease.MessageText);
        var foreignRun = await OwnerErrorAsync(worldA, InsertRunSql(worldA, releaseA, runB, "api"));
        foreignRun.SqlState.ShouldBe("23503", foreignRun.MessageText);

        (await _fixture.ScalarAsync<long>(
            worldA.Database,
            $"select count(*) from public.release_run where org_id = '{worldA.Org:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task ReleaseRun_RejectsDuplicateTuple_WithDistinctComponentAndReleasePositiveControls()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseOne = await CreateReleaseAsync(world, "release-tuple-one", "active", OneComponent(world));
        var releaseTwo = await CreateReleaseAsync(world, "release-tuple-two", "active", OneComponent(world, fill: 'b'));
        var run = await CancelRunTestData.CreateQueuedRunAsync(world);

        (await OwnerExecuteAsync(world, InsertRunSql(world, releaseOne, run, "web"))).ShouldBe(1);
        var duplicate = await OwnerErrorAsync(world, InsertRunSql(world, releaseOne, run, "web"));
        duplicate.SqlState.ShouldBe("23505", duplicate.MessageText);
        (await OwnerExecuteAsync(world, InsertRunSql(world, releaseOne, run, "api"))).ShouldBe(1);
        (await OwnerExecuteAsync(world, InsertRunSql(world, releaseTwo, run, "web"))).ShouldBe(1);

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.release_run where org_id = '{world.Org:D}'")).ShouldBe(3);
    }

    /// <summary>One <c>release_component</c> insert for the world's org, spelled for the scope that runs it.</summary>
    private static string InsertComponentSql(RunCreationTests.World world, Guid releaseId, string key, string digest) =>
        $"insert into public.release_component (id, org_id, release_id, component_key, digest, environment_revision_id) "
        + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{releaseId:D}', '{key}', '{digest}', "
        + $"'{world.EnvironmentRevision:D}')";

    /// <summary>One <c>release_run</c> insert for the world's org.</summary>
    private static string InsertRunSql(RunCreationTests.World world, Guid releaseId, Guid runId, string componentKey) =>
        $"insert into public.release_run (id, org_id, release_id, run_id, component_key) "
        + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{releaseId:D}', '{runId:D}', '{componentKey}')";

    /// <summary>One component's id, read as the migration role.</summary>
    [Fact]
    public async Task ReleaseComponent_ConcurrentActivationBlocksTheInsert_ThenRefusesIt()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await CreateReleaseAsync(world, "release-race", null, OneComponent(world)[0]);

        // Transaction 1 activates the release and stays open, so its UPDATE holds the parent row.
        await using var activating = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(world.Database), world.Org, world.Project);
        await activating.ExecuteAsync(
            $"update public.release set state = 'active' where id = '{releaseId:D}'");

        // Transaction 2's INSERT must wait on the parent row: the guard's `for share` is what makes it wait, and a
        // bounded `statement_timeout` proves the wait without a sleep (57014 is the cancel the wait ends in).
        await using (var blocked = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(world.Database), world.Org, world.Project))
        {
            await blocked.ExecuteAsync("set local statement_timeout = '2s'");
            var wait = await Should.ThrowAsync<PostgresException>(() => blocked.ExecuteAsync(
                $"insert into public.release_component (id, org_id, release_id, component_key, digest, environment_revision_id) "
                + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{releaseId:D}', 'web-race', "
                + $"'{Digest('c')}', '{world.EnvironmentRevision:D}')"));
            wait.SqlState.ShouldBe(PostgresErrorCodes.QueryCanceled, wait.MessageText);
        }

        // Once the activation commits, the same INSERT is refused because the release is closed.
        await activating.CommitAsync();
        var closed = await AppErrorAsync(
            world,
            $"insert into public.release_component (id, org_id, release_id, component_key, digest, environment_revision_id) "
            + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{releaseId:D}', 'web-race', "
            + $"'{Digest('c')}', '{world.EnvironmentRevision:D}')");
        closed.SqlState.ShouldBe("23514");
        closed.MessageText.ShouldContain("release_component_closed");
        (await ComponentCountAsync(world, releaseId)).ShouldBe(1);
    }

    [Fact]
    public async Task ReleaseComponent_RefusesASiblingProjectRevision_WithSameProjectControl()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await CreateReleaseAsync(world, "release-project-scope", null, OneComponent(world)[0]);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("sibling"));
        var siblingRevision = await SeedRevisionAsync(world, sibling);

        var refused = await AppErrorAsync(
            world,
            $"insert into public.release_component (id, org_id, release_id, component_key, digest, environment_revision_id) "
            + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{releaseId:D}', 'api', '{Digest('d')}', '{siblingRevision:D}')");
        refused.SqlState.ShouldBe("23514");
        refused.MessageText.ShouldContain("release_component_project");

        // The same INSERT with the project's own revision is accepted: only the variable under test changed.
        var accepted = await AppExecuteAsync(
            world,
            $"insert into public.release_component (id, org_id, release_id, component_key, digest, environment_revision_id) "
            + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{releaseId:D}', 'api', '{Digest('d')}', '{world.EnvironmentRevision:D}')");
        accepted.ShouldBe(1);
        (await ComponentCountAsync(world, releaseId)).ShouldBe(2);
    }

    [Fact]
    public async Task Release_ActivationWithoutComponents_IsRefused()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = Guid.CreateVersion7();
        await OwnerExecuteAsync(
            world,
            $"insert into public.release (id, org_id, project_id, name, created_by) "
            + $"values ('{releaseId:D}', '{world.Org:D}', '{world.Project:D}', 'release-empty', 'probe')");

        var refused = await OwnerErrorAsync(
            world,
            $"update public.release set state = 'active' where id = '{releaseId:D}'");
        refused.SqlState.ShouldBe("23514");
        refused.MessageText.ShouldContain("at least one component");
        (await ReleaseScalarAsync(world, releaseId, "state")).ShouldBe("draft");
    }

    private async Task<Guid> ComponentIdAsync(RunCreationTests.World world, Guid releaseId, string key) =>
        await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.release_component where org_id = '{world.Org:D}' "
            + $"and release_id = '{releaseId:D}' and component_key = '{key}'").ConfigureAwait(false);

    /// <summary>One component's digest, read as the migration role.</summary>
    private async Task<string> ComponentDigestAsync(RunCreationTests.World world, Guid componentId) =>
        await _fixture.ScalarAsync<string>(
            world.Database,
            $"select digest from public.release_component where id = '{componentId:D}'").ConfigureAwait(false);

    /// <summary>One release column as text, read as the migration role.</summary>
    private async Task<string> ReleaseScalarAsync(RunCreationTests.World world, Guid releaseId, string column) =>
        await _fixture.ScalarAsync<string>(
            world.Database,
            $"select {column}::text from public.release where id = '{releaseId:D}'").ConfigureAwait(false);

    /// <summary>One statement as the app role under the world's tenant context, committed.</summary>
    private async Task<int> AppExecuteAsync(RunCreationTests.World world, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(world.Database), world.Org, world.Project);
        var affected = await scope.ExecuteAsync(sql);
        await scope.CommitAsync();

        return affected;
    }

    /// <summary>One statement that must be refused, as the app role under the world's tenant context.</summary>
    private async Task<PostgresException> AppErrorAsync(RunCreationTests.World world, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(world.Database), world.Org, world.Project);

        return await scope.CaptureErrorAsync(sql);
    }

    /// <summary>One statement that must be refused, as the migration role.</summary>
    private async Task<PostgresException> OwnerErrorAsync(RunCreationTests.World world, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(
            _fixture.OwnerConnectionString(world.Database), world.Org, world.Project);

        return await scope.CaptureErrorAsync(sql);
    }

    /// <summary>One statement as the migration role, committed.</summary>
    private async Task<int> OwnerExecuteAsync(RunCreationTests.World world, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(
            _fixture.OwnerConnectionString(world.Database), world.Org, world.Project);
        var affected = await scope.ExecuteAsync(sql);
        await scope.CommitAsync();

        return affected;
    }
}
