using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Migration 0102 through a real PostgreSQL: a <c>run</c> row must name a project of its own org, and every
/// manifest, environment revision, policy revision and suite revision it references must belong to that project.
/// Rows are written as the app role, so the keys, the suite-revision trigger and RLS all apply.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RunProjectKeyTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;

    public RunProjectKeyTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    private sealed record Refs(Guid Manifest, Guid SuiteRevision, Guid EnvironmentRevision, Guid PolicyRevision);

    private sealed record World(string Database, Guid Org, Guid ProjectA, Guid ProjectB, Refs A, Refs B, HttpClient Client, TenancyApiFactory Api)
        : IAsyncDisposable
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
        var api = _hosts.RealResolver(database);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var a = await CreateProjectAsync(client, user, org, "alpha");
        var b = await CreateProjectAsync(client, user, org, "beta");

        return new World(database, org, a, b, await SeedRefsAsync(database, org, a), await SeedRefsAsync(database, org, b), client, api);
    }

    /// <summary>The four referenced rows of one project, written as the migration role.</summary>
    private async Task<Refs> SeedRefsAsync(string database, Guid org, Guid project)
    {
        var suite = Guid.CreateVersion7();
        var suiteRevision = Guid.CreateVersion7();
        var environment = Guid.CreateVersion7();
        var environmentRevision = Guid.CreateVersion7();
        var manifest = Guid.CreateVersion7();
        var policyRevision = Guid.CreateVersion7();
        var hash = "sha256:" + new string('a', 64);

        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await scope.ExecuteAsync(
            "insert into public.suite (id, org_id, project_id, name, slug) values (@id, @org, @project, 'probe', 'probe-suite')",
            ("id", suite), ("org", org), ("project", project));
        await scope.ExecuteAsync(
            """
            insert into public.suite_revision (id, org_id, suite_id, revision_no, revision_hash, shard_strategy, retry_policy)
            values (@id, @org, @suite, 1, @hash, '{"kind":"none"}', '{"max_retries":0}')
            """,
            ("id", suiteRevision), ("org", org), ("suite", suite), ("hash", hash));
        await scope.ExecuteAsync(
            "insert into public.environment (id, org_id, project_id, environment_key, name) values (@id, @org, @project, 'staging', 'Staging')",
            ("id", environment), ("org", org), ("project", project));
        await scope.ExecuteAsync(
            """
            insert into public.environment_revision (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, revision_hash, created_by)
            values (@id, @org, @project, @environment, 1, '{}', '{}', @hash, 'probe')
            """,
            ("id", environmentRevision), ("org", org), ("project", project), ("environment", environment), ("hash", hash));
        await scope.ExecuteAsync(
            """
            insert into public.manifest (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id, shard_plan, matrix, env_needs, planned_count, document, verified_by)
            values (@id, @org, @project, @hash, 1, @suite_revision, @environment_revision, '{}', '{}', '{}', 0, '{}', 'probe')
            """,
            ("id", manifest), ("org", org), ("project", project), ("hash", hash),
            ("suite_revision", suiteRevision), ("environment_revision", environmentRevision));
        await scope.ExecuteAsync(
            "insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by) values (@id, @org, @project, 1, @hash, '{}', @by)",
            ("id", policyRevision), ("org", org), ("project", project), ("hash", hash), ("by", Guid.CreateVersion7()));
        await scope.CommitAsync();

        return new Refs(manifest, suiteRevision, environmentRevision, policyRevision);
    }

    private static string InsertRunSql(Guid id, Guid org, Guid project, Refs refs, Guid? policy) =>
        "insert into public.run (id, org_id, project_id, manifest_id, policy_revision_id, suite_revision_id, environment_revision_id, "
        + "collection_deadline_at, late_window_minutes) "
        + $"values ('{id}', '{org}', '{project}', '{refs.Manifest}', {(policy is null ? "null" : $"'{policy}'")}, "
        + $"'{refs.SuiteRevision}', '{refs.EnvironmentRevision}', now() + interval '2 hours', 1440)";

    private async Task<PostgresException> RefusedAsync(Guid actingOrg, string database, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), actingOrg);

        return await scope.CaptureErrorAsync(sql);
    }

    [Fact]
    public async Task Valid_Run_With_And_Without_A_Policy_Revision_Inserts()
    {
        await using var world = await StartAsync();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(InsertRunSql(Guid.CreateVersion7(), world.Org, world.ProjectA, world.A, world.A.PolicyRevision))).ShouldBe(1);
        (await scope.ExecuteAsync(InsertRunSql(Guid.CreateVersion7(), world.Org, world.ProjectA, world.A, null))).ShouldBe(1);
        await scope.CommitAsync();
    }

    [Fact]
    public async Task Run_Project_Of_Another_Org_Is_Refused()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("beta"));
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "other");
        var theirs = await SeedRefsAsync(world.Database, otherOrg, otherProject);

        // Filed under this org (RLS passes) naming another org's project, with that project's own parents: the database refuses it, and the BEFORE trigger answers first.
        var error = await RefusedAsync(world.Org, world.Database, InsertRunSql(Guid.CreateVersion7(), world.Org, otherProject, theirs, null));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("run_suite_revision_project_guard", "the BEFORE trigger fires ahead of the AFTER foreign keys");
    }

    [Theory]
    [InlineData("manifest", "run_manifest_fk")]
    [InlineData("environment", "run_environment_revision_fk")]
    [InlineData("policy", "run_policy_revision_fk")]
    [InlineData("suite", "run_suite_revision_project_guard")]
    public async Task Run_Reference_From_Another_Project_Is_Refused(string which, string constraint)
    {
        await using var world = await StartAsync();
        var mixed = which switch
        {
            "manifest" => world.A with { Manifest = world.B.Manifest },
            "environment" => world.A with { EnvironmentRevision = world.B.EnvironmentRevision },
            "suite" => world.A with { SuiteRevision = world.B.SuiteRevision },
            _ => world.A,
        };
        var policy = which == "policy" ? world.B.PolicyRevision : world.A.PolicyRevision;

        var error = await RefusedAsync(world.Org, world.Database, InsertRunSql(Guid.CreateVersion7(), world.Org, world.ProjectA, mixed, policy));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe(constraint);
    }

    [Fact]
    public async Task Suite_Revision_Of_Another_Org_Is_Refused()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("gamma"));
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "other");
        var theirs = await SeedRefsAsync(world.Database, otherOrg, otherProject);

        var error = await RefusedAsync(
            world.Org, world.Database, InsertRunSql(Guid.CreateVersion7(), world.Org, world.ProjectA, world.A with { SuiteRevision = theirs.SuiteRevision }, null));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("run_suite_revision_project_guard");
    }

    [Fact]
    public async Task Update_To_A_Suite_Revision_Of_Another_Project_Is_Refused_As_The_Migration_Role()
    {
        await using var world = await StartAsync();
        var runId = Guid.CreateVersion7();
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertRunSql(runId, world.Org, world.ProjectA, world.A, null))).ShouldBe(1);
            await scope.CommitAsync();
        }

        // The app role holds no column grant here; the owner proves the trigger holds for every role.
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        var error = await owner.CaptureErrorAsync(
            $"update public.run set suite_revision_id = '{world.B.SuiteRevision}' where id = '{runId}'");

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("run_suite_revision_project_guard");
    }

    [Theory]
    [InlineData("project_id")]
    [InlineData("suite_revision_id")]
    [InlineData("manifest_id")]
    [InlineData("environment_revision_id")]
    [InlineData("policy_revision_id")]
    public async Task Update_Of_A_Reference_To_Another_Project_Is_Refused_For_The_Owner_Too(string column)
    {
        await using var world = await StartAsync();
        var runId = Guid.CreateVersion7();
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertRunSql(runId, world.Org, world.ProjectA, world.A, world.A.PolicyRevision))).ShouldBe(1);
            await scope.CommitAsync();
        }

        var bad = column switch
        {
            "project_id" => world.ProjectB,
            "suite_revision_id" => world.B.SuiteRevision,
            "manifest_id" => world.B.Manifest,
            "environment_revision_id" => world.B.EnvironmentRevision,
            _ => world.B.PolicyRevision,
        };
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        var error = await owner.CaptureErrorAsync($"update public.run set {column} = '{bad}' where id = '{runId}'");

        error.SqlState.ShouldBe("23503");
    }

    [Theory]
    [InlineData("environment", "run_environment_revision_fk")]
    [InlineData("policy", "run_policy_revision_fk")]
    public async Task Run_Reference_From_Another_Org_Is_Refused(string which, string constraint)
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("foreign"));
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "other");
        var theirs = await SeedRefsAsync(world.Database, otherOrg, otherProject);
        var refs = which == "environment" ? world.A with { EnvironmentRevision = theirs.EnvironmentRevision } : world.A;
        var policy = which == "policy" ? theirs.PolicyRevision : world.A.PolicyRevision;

        var error = await RefusedAsync(world.Org, world.Database, InsertRunSql(Guid.CreateVersion7(), world.Org, world.ProjectA, refs, policy));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe(constraint);
    }
}
