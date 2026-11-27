using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The database side of the requirement link table: target check, keys, uniqueness, grants, isolation, purge order.</summary>
[Trait("Category", "Docker")]
public sealed class RequirementLinkSchemaTests
{
    private readonly PostgresTenancyFixture _fixture;

    public RequirementLinkSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task OneTargetCheck_RefusesNoTargetAndBothTargets_AndAcceptsEachSingleTarget()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);
        var app = _fixture.AppConnectionString(world.Database);

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            var none = await scope.CaptureErrorAsync(RequirementLinkTestData.InsertSql(world, head.RequirementId, head.VersionId, null, null));
            none.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            none.ConstraintName.ShouldBe("requirement_link_one_target_check");
        }

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            var both = await scope.CaptureErrorAsync(RequirementLinkTestData.InsertSql(world, head.RequirementId, head.VersionId, targets.Item, targets.ApprovedCase));
            both.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            both.ConstraintName.ShouldBe("requirement_link_one_target_check");
        }

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            (await scope.ExecuteAsync(RequirementLinkTestData.InsertSql(world, head.RequirementId, head.VersionId, targets.Item, null))).ShouldBe(1);
            (await scope.ExecuteAsync(RequirementLinkTestData.InsertSql(world, head.RequirementId, head.VersionId, null, targets.ApprovedCase))).ShouldBe(1);
            await scope.CommitAsync();
        }
    }

    [Fact]
    public async Task Keys_RefuseAForeignProjectTarget_AForeignRequirementVersion_AndAForeignProjectRequirement()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var sibling = await world.CreatedAsync(world.Create());
        var foreign = await world.CreatedAsync(world.Create(project: world.OtherProject));
        var own = await RequirementLinkTestData.SeedAsync(world);
        var other = await RequirementLinkTestData.SeedAsync(world, world.OtherProject);
        var app = _fixture.AppConnectionString(world.Database);

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            var item = await scope.CaptureErrorAsync(RequirementLinkTestData.InsertSql(world, head.RequirementId, head.VersionId, other.Item, null));
            item.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
            item.ConstraintName.ShouldBe("requirement_link_item_fk");
        }

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            var manual = await scope.CaptureErrorAsync(RequirementLinkTestData.InsertSql(world, head.RequirementId, head.VersionId, null, other.ApprovedCase));
            manual.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
            manual.ConstraintName.ShouldBe("requirement_link_case_fk");
        }

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            var version = await scope.CaptureErrorAsync(RequirementLinkTestData.InsertSql(world, head.RequirementId, sibling.VersionId, own.Item, null));
            version.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
            version.ConstraintName.ShouldBe("requirement_link_version_fk");
        }

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            var requirement = await scope.CaptureErrorAsync(RequirementLinkTestData.InsertSql(world, foreign.RequirementId, foreign.VersionId, own.Item, null));
            requirement.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
            requirement.ConstraintName.ShouldBe("requirement_link_requirement_fk");
        }

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            // Positive control: the same requirement and version with an own-project target is accepted.
            (await scope.ExecuteAsync(RequirementLinkTestData.InsertSql(world, head.RequirementId, head.VersionId, own.Item, null))).ShouldBe(1);
        }
    }

    [Fact]
    public async Task Duplicate_RefusesASecondLinkOfTheSameTarget_AndNotOfAnotherTarget()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);
        var app = _fixture.AppConnectionString(world.Database);

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            await scope.ExecuteAsync(RequirementLinkTestData.InsertSql(world, head.RequirementId, head.VersionId, targets.Item, null));
            await scope.ExecuteAsync(RequirementLinkTestData.InsertSql(world, head.RequirementId, head.VersionId, null, targets.ApprovedCase));
            await scope.CommitAsync();
        }

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            var again = await scope.CaptureErrorAsync(RequirementLinkTestData.InsertSql(world, head.RequirementId, head.VersionId, targets.Item, null));
            again.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
            again.ConstraintName.ShouldBe("requirement_link_target_key");
        }

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            var again = await scope.CaptureErrorAsync(RequirementLinkTestData.InsertSql(world, head.RequirementId, head.VersionId, null, targets.ApprovedCase));
            again.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
            again.ConstraintName.ShouldBe("requirement_link_target_key");
        }

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            // Positive control: another case of the same item is a different target.
            (await scope.ExecuteAsync(RequirementLinkTestData.InsertSql(world, head.RequirementId, head.VersionId, null, targets.DraftCase))).ShouldBe(1);
        }
    }

    [Fact]
    public async Task AppRole_CannotUpdateALink_ButCanDeleteIt_AndTheOtherRolesHoldNothing()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);
        var link = await RequirementLinkTestData.LinkAsync(world, head, targets.Item);
        var app = _fixture.AppConnectionString(world.Database);

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            var update = await scope.CaptureErrorAsync($"update public.requirement_link set manual_test_case_id = null where id = '{link.LinkId:D}'");
            update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "requirement_link", "UPDATE")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "requirement_link", "SELECT")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "requirement_link", "INSERT")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_operator", "requirement_link", "SELECT")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_worker_sched", "requirement_link", "SELECT")).ShouldBeFalse();

        await using var remover = await TenantScope.BeginAsync(app, world.Org);
        (await remover.ExecuteAsync($"delete from public.requirement_link where id = '{link.LinkId:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Rows_AreInvisibleToAnotherTenantAndToAScopeWithoutContext_AndOwnTenantSeesItsRow()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        await using var other = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);
        await RequirementLinkTestData.LinkAsync(world, head, targets.Item);
        var app = _fixture.AppConnectionString(world.Database);

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            (await scope.ScalarAsync<long>("select count(*) from public.requirement_link")).ShouldBe(1);
        }

        await using (var scope = await TenantScope.BeginAsync(app, other.Org))
        {
            (await scope.ScalarAsync<long>("select count(*) from public.requirement_link")).ShouldBe(0);
            var forged = await scope.CaptureErrorAsync(RequirementLinkTestData.InsertSql(world, head.RequirementId, head.VersionId, null, targets.DraftCase));
            forged.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using var bare = await TenantScope.BeginAsync(app);
        (await bare.ScalarAsync<long>("select count(*) from public.requirement_link")).ShouldBe(0);
    }

    [Fact]
    public async Task Purge_DeletesLinksFirst_ThenVersionsAndRequirements_AsTheOwner()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);
        await RequirementLinkTestData.LinkAsync(world, head, targets.Item);
        await RequirementLinkTestData.LinkAsync(world, head, manualCase: targets.DraftCase);
        var owner = _fixture.OwnerConnectionString(world.Database);

        await using (var scope = await TenantScope.BeginAsync(owner, world.Org))
        {
            var version = await scope.CaptureErrorAsync($"delete from public.requirement_version where org_id = '{world.Org:D}'");
            version.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        }

        await using (var scope = await TenantScope.BeginAsync(owner, world.Org))
        {
            var requirement = await scope.CaptureErrorAsync($"delete from public.requirement where org_id = '{world.Org:D}'");
            requirement.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        }

        await using (var scope = await TenantScope.BeginAsync(owner, world.Org))
        {
            (await scope.ExecuteAsync($"delete from public.requirement_link where org_id = '{world.Org:D}'")).ShouldBe(2);
            await scope.ExecuteAsync($"delete from public.requirement_version where org_id = '{world.Org:D}'");
            await scope.ExecuteAsync($"delete from public.requirement where org_id = '{world.Org:D}'");
            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.requirement_link where org_id = '{world.Org:D}'")).ShouldBe(0);
    }
}
