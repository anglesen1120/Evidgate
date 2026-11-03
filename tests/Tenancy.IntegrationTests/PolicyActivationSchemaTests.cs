using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.1.1-T4.1 SQL level, as <c>evidgate_app</c> through the real raw-SQL helpers: the grants and RLS of
/// <c>policy_activation</c> (no DELETE, an UPDATE limited to the four re-stamped columns, FORCE RLS, the
/// tenant <c>WITH CHECK</c> and the composite FK to <c>policy_revision</c>). The <c>policy_revision</c> side
/// of the same migration (the <c>UPDATE(state)</c> grant, the state machine and the one-active index) lives in
/// <c>PolicyRevisionImmutabilityTests</c>, next to the table's existing append-only suite.
/// </summary>
/// <remarks>
/// The <c>policy_revision</c> and <c>policy_activation</c> rows are seeded as the app role in one tenant
/// transaction, exactly as the activation store writes them, so the grants under test are the ones in force.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class PolicyActivationSchemaTests
{
    private readonly PostgresTenancyFixture _fixture;

    public PolicyActivationSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        return new World(api, client, database, user, org, project);
    }

    /// <summary>A well-formed content address of the requested fill character.</summary>
    private static string Hash(char fill) => "sha256:" + new string(fill, 64);

    private static string InsertRevisionSql(Guid id, Guid org, Guid project, int no, string hash) =>
        "insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, state, created_by, version) " +
        $"values ('{id}', '{org}', '{project}', {no}, '{hash}', '{{}}'::jsonb, 'draft', '{Guid.CreateVersion7()}', 1)";

    private static string InsertActivationSql(Guid id, Guid org, Guid project, Guid revision) =>
        "insert into public.policy_activation (id, org_id, project_id, policy_revision_id, mode, activated_by) " +
        $"values ('{id}', '{org}', '{project}', '{revision}', 'advisory', '{Guid.CreateVersion7()}')";

    /// <summary>A draft revision and the project's pointer to it, written as the app role in one transaction.</summary>
    private async Task<(Guid Revision, Guid Activation)> SeedPointerAsync(World world)
    {
        var revision = Guid.CreateVersion7();
        var activation = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        await scope.ExecuteAsync(InsertRevisionSql(revision, world.Org, world.Project, 1, Hash('a')));
        await scope.ExecuteAsync(InsertActivationSql(activation, world.Org, world.Project, revision));
        await scope.CommitAsync();

        return (revision, activation);
    }

    // ------------------------------------------------------------------ row security

    [Fact]
    public async Task Pointer_IsUnderForcedRowSecurity()
    {
        await using var world = await StartAsync();

        (await _fixture.TableExistsAsync(world.Database, "policy_activation")).ShouldBeTrue();
        var rls = await _fixture.RowSecurityAsync(world.Database, "policy_activation");
        rls.Enabled.ShouldBeTrue();
        rls.Forced.ShouldBeTrue();
    }

    // ------------------------------------------------------------------ the app-role grants

    [Fact]
    public async Task AppRole_CannotDeleteThePointer_AtTheDatabase()
    {
        await using var world = await StartAsync();
        await SeedPointerAsync(world);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync("delete from public.policy_activation")).SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task AppRole_CanUpdateOnlyTheFourReStampedColumns()
    {
        await using var world = await StartAsync();
        var (_, activation) = await SeedPointerAsync(world);
        var where = $"where id = '{activation}'";

        // The granted set: policy_revision_id, mode, activated_by, activated_at (migration 0033).
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync($"update public.policy_activation set activated_at = now() {where}")).ShouldBe(1);
            await scope.CommitAsync();
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync($"update public.policy_activation set mode = 'advisory' {where}")).ShouldBe(1);
            await scope.CommitAsync();
        }

        // Everything else is outside the grant: 42501 before any trigger or constraint runs.
        foreach (var set in new[]
        {
            $"org_id = '{Guid.CreateVersion7()}'",
            $"project_id = '{Guid.CreateVersion7()}'",
            $"id = '{Guid.CreateVersion7()}'",
            "created_at = now()",
            "updated_at = now()",
            "version = 5",
        })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
            (await scope.CaptureErrorAsync($"update public.policy_activation set {set} {where}")).SqlState.ShouldBe("42501", set);
        }
    }

    // ------------------------------------------------------------------ the tenant boundary

    [Fact]
    public async Task AnotherOrg_ReadsZeroPointerRows()
    {
        await using var world = await StartAsync();
        await SeedPointerAsync(world);

        await using var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(world.Database),
            Guid.CreateVersion7());

        (await scope.ScalarAsync<long>("select count(*) from public.policy_activation")).ShouldBe(0);
    }

    [Fact]
    public async Task AppRole_CannotInsertAPointerForAnotherOrg_WithCheck()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("beta"));
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, Unique.Slug("other"));
        var otherRevision = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(world.Database, InsertRevisionSql(otherRevision, otherOrg, otherProject, 1, Hash('b')));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        // The row is fully consistent apart from the tenant: only the RLS WITH CHECK can refuse it, so the
        // 42501 is the tenant boundary and not a constraint.
        (await scope.CaptureErrorAsync(InsertActivationSql(Guid.CreateVersion7(), otherOrg, otherProject, otherRevision)))
            .SqlState.ShouldBe("42501");
    }

    // ------------------------------------------------------------------ the composite FK

    [Fact]
    public async Task CompositeFk_RefusesARevisionOfAnotherTenant()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("beta"));
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, Unique.Slug("other"));
        var foreignRevision = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(world.Database, InsertRevisionSql(foreignRevision, otherOrg, otherProject, 1, Hash('b')));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        // The FK is (org_id, policy_revision_id) -> policy_revision (org_id, id): another tenant's revision
        // has no row under this org, so the pointer cannot name it.
        (await scope.CaptureErrorAsync(InsertActivationSql(Guid.CreateVersion7(), world.Org, world.Project, foreignRevision)))
            .SqlState.ShouldBe("23503");
    }

    /// <summary>
    /// Boundary of the composite FK: it is org-scoped only — <c>(org_id, policy_revision_id)</c> references
    /// <c>policy_revision (org_id, id)</c>, with no <c>project_id</c> component (migration 0033:57-58). A revision
    /// of another project in the same org therefore satisfies the FK; the project match is the activation store's
    /// read predicate (<c>PolicyActivationSql.RevisionById</c>), not the database's.
    /// </summary>
    [Fact]
    public async Task CompositeFk_IsOrgScoped_SoASameOrgOtherProjectRevisionIsAccepted()
    {
        await using var world = await StartAsync();
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("second"));
        var foreignRevision = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(world.Database, InsertRevisionSql(foreignRevision, world.Org, otherProject, 1, Hash('c')));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(InsertActivationSql(Guid.CreateVersion7(), world.Org, world.Project, foreignRevision))).ShouldBe(1);
        await scope.CommitAsync();

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.policy_activation where org_id = '{world.Org:D}'")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ the FK's own guard

    [Fact]
    public async Task CompositeFk_RefusesAnUnknownRevision()
    {
        await using var world = await StartAsync();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(InsertActivationSql(Guid.CreateVersion7(), world.Org, world.Project, Guid.CreateVersion7())))
            .SqlState.ShouldBe("23503");
    }
}
