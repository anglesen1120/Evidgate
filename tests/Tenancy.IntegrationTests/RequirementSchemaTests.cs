using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The database side of the requirement tables: immutability, tenant isolation, cycle guard, purge order, history index.</summary>
[Trait("Category", "Docker")]
public sealed class RequirementSchemaTests
{
    private readonly PostgresTenancyFixture _fixture;

    public RequirementSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AppRole_CannotUpdateOrDeleteAVersion_AndHoldsNoDeleteOnTheIdentity()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var app = _fixture.AppConnectionString(world.Database);

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            var update = await scope.CaptureErrorAsync($"update public.requirement_version set body = 'x' where id = '{head.VersionId:D}'");
            update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            var delete = await scope.CaptureErrorAsync($"delete from public.requirement_version where id = '{head.VersionId:D}'");
            delete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "requirement", "DELETE")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "requirement_version", "UPDATE")).ShouldBeFalse();
    }

    [Fact]
    public async Task Trigger_RefusesAVersionUpdateEvenForTheOwner()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org);
        var update = await owner.CaptureErrorAsync($"update public.requirement_version set reason = 'edit' where id = '{head.VersionId:D}'");

        update.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Tree_RefusesACycleAndASelfParent()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var a = await world.CreatedAsync(world.Create("epic", "A"));
        var b = await world.CreatedAsync(world.Create("user_story", "B", parent: a.RequirementId));
        var c = await world.CreatedAsync(world.Create("acceptance_criterion", "C", parent: b.RequirementId));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var cycle = await scope.CaptureErrorAsync($"update public.requirement set parent_requirement_id = '{c.RequirementId:D}' where id = '{a.RequirementId:D}'");
        cycle.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);

        await using var second = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var self = await second.CaptureErrorAsync($"update public.requirement set parent_requirement_id = id where id = '{a.RequirementId:D}'");
        self.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Rows_AreInvisibleToAnotherTenantAndToAScopeWithoutContext()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        await using var other = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var app = _fixture.AppConnectionString(world.Database);

        await using (var scope = await TenantScope.BeginAsync(app, other.Org))
        {
            (await scope.ScalarAsync<long>("select count(*) from public.requirement")).ShouldBe(0);
            (await scope.ScalarAsync<long>("select count(*) from public.requirement_version")).ShouldBe(0);
            var forged = await scope.CaptureErrorAsync(
                $"insert into public.requirement_version (id, org_id, project_id, requirement_id, version_no, body, content_hash, source) values (gen_random_uuid(), '{world.Org:D}', '{world.Project:D}', '{head.RequirementId:D}', 9, '', 'sha256:{new string('a', 64)}', 'manual')");
            forged.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using var bare = await TenantScope.BeginAsync(app);
        (await bare.ScalarAsync<long>("select count(*) from public.requirement")).ShouldBe(0);
        (await bare.ScalarAsync<long>("select count(*) from public.requirement_version")).ShouldBe(0);
    }

    [Fact]
    public async Task Purge_DeletesVersionsFirstThenRequirements_AsTheOwner()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var parent = await world.CreatedAsync(world.Create("epic", "P"));
        await world.CreatedAsync(world.Create("user_story", "K", parent: parent.RequirementId));

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org))
        {
            var early = await scope.CaptureErrorAsync($"delete from public.requirement where org_id = '{world.Org:D}'");
            early.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org))
        {
            await scope.ExecuteAsync($"delete from public.requirement_version where org_id = '{world.Org:D}'");
            await scope.ExecuteAsync($"delete from public.requirement where org_id = '{world.Org:D}'");
            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.requirement where org_id = '{world.Org:D}'")).ShouldBe(0);
    }

    [Fact]
    public async Task HistoryRead_UsesTheUniqueKeyIndex_WithoutASortOrSeqScan()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());

        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org);
        await scope.ExecuteAsync("set local enable_seqscan = off");
        var plan = await scope.ScalarAsync<string>(
            "explain (format json) select id from public.requirement_version where org_id = @org and requirement_id = @id order by version_no desc limit 50",
            ("org", world.Org),
            ("id", head.RequirementId));

        plan.ShouldContain("requirement_version_no_key", Case.Sensitive);
        plan.ShouldNotContain("\"Node Type\": \"Sort\"", Case.Sensitive);
    }
}
