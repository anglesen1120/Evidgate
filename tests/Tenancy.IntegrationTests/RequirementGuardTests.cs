using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>Input guards of the writer, direct-SQL pins of the keys and the head step, and positive controls.</summary>
[Trait("Category", "Docker")]
public sealed class RequirementGuardTests
{
    private readonly PostgresTenancyFixture _fixture;

    public RequirementGuardTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private const string Hash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Text_WithNulOrAnUnpairedSurrogate_Is422NotAnException()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());

        foreach (var bad in new[] { "a\0b", "lone\ud800" })
        {
            var body = await world.InScopeAsync((writer, token) => writer.CreateAsync(world.Org, world.Create(body: bad), token));
            var title = await world.InScopeAsync((writer, token) => writer.CreateAsync(world.Org, world.Create(title: bad), token));
            var reason = await world.InScopeAsync((writer, token) => writer.AppendVersionAsync(world.Org, world.Project, head.RequirementId, head.RowVersion, "ok", "manual", null, bad, token));

            body.Error!.Details!.ShouldContain(detail => detail.Path == "/body");
            title.Error!.Details!.ShouldContain(detail => detail.Path == "/title");
            reason.Error!.Details!.ShouldContain(detail => detail.Path == "/reason");
        }
    }

    [Fact]
    public async Task Create_WithALiveFeatureNode_AndReparent_AreAccepted()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var node = Guid.CreateVersion7();
        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            await seed.ExecuteAsync(
                "insert into public.feature_node (id, org_id, project_id, kind, title, position) values (@id, @org_id, @project_id, 'module', 'Shop', 0)",
                ("id", node),
                ("org_id", world.Org),
                ("project_id", world.Project));
            await seed.CommitAsync();
        }

        var epic = await world.CreatedAsync(world.Create("epic", "E", featureNode: node));
        var story = await world.CreatedAsync(world.Create("user_story", "S"));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            "update public.requirement set parent_requirement_id = @parent where id = @id",
            ("parent", epic.RequirementId),
            ("id", story.RequirementId))).ShouldBe(1);
        (await scope.ScalarAsync<long>("select count(*) from public.requirement where id = @id", ("id", epic.RequirementId))).ShouldBe(1);
    }

    [Fact]
    public async Task Keys_RefuseAWrongProjectParent_AWrongProjectVersion_AndADuplicateVersionNumber()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var foreign = await world.CreatedAsync(world.Create(project: world.OtherProject));
        var app = _fixture.AppConnectionString(world.Database);

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            var error = await scope.CaptureErrorAsync(
                "update public.requirement set parent_requirement_id = @parent where id = @id",
                ("parent", foreign.RequirementId),
                ("id", head.RequirementId));
            error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
            error.ConstraintName.ShouldNotBeNull().ShouldContain("parent");
        }

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            var error = await scope.CaptureErrorAsync(InsertVersion(world, world.OtherProject, head.RequirementId, 7));
            error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        }

        await using (var scope = await TenantScope.BeginAsync(app, world.Org))
        {
            var error = await scope.CaptureErrorAsync(InsertVersion(world, world.Project, head.RequirementId, 1));
            error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
            error.ConstraintName.ShouldBe("requirement_version_no_key");
        }
    }

    [Fact]
    public async Task HeadNumber_MovesOnlyForwardByOne()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var app = _fixture.AppConnectionString(world.Database);

        await using (var same = await TenantScope.BeginAsync(app, world.Org))
        {
            (await same.ExecuteAsync("update public.requirement set head_version_no = head_version_no where id = @id", ("id", head.RequirementId))).ShouldBe(1);
        }

        await using var skip = await TenantScope.BeginAsync(app, world.Org);
        var error = await skip.CaptureErrorAsync("update public.requirement set head_version_no = head_version_no + 2 where id = @id", ("id", head.RequirementId));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Reads_OfAForeignProjectOrTenant_AreEmptyOrNotFound_AndOwnTenantSeesItsRow()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());

        var sibling = await world.InScopeAsync((writer, token) => writer.ListVersionsAsync(world.Org, world.OtherProject, head.RequirementId, null, 10, token), world.OtherProject, commit: false);
        var missing = await world.InScopeAsync((writer, token) => writer.GetAsync(world.Org, world.Project, Guid.CreateVersion7(), token), commit: false);
        var siblingGet = await world.InScopeAsync((writer, token) => writer.GetAsync(world.Org, world.OtherProject, head.RequirementId, token), world.OtherProject, commit: false);
        var own = await world.InScopeAsync((writer, token) => writer.ListVersionsAsync(world.Org, world.Project, head.RequirementId, null, 10, token), commit: false);

        sibling.Value.ShouldBeEmpty();
        missing.Error!.Status.ShouldBe(404);
        siblingGet.Error!.Status.ShouldBe(404);
        own.Value.Count.ShouldBe(1);
        own.Value[0].ContentHash.ShouldBe(head.ContentHash);
        own.Value[0].Body.ShouldBe(RequirementTestWorld.Body);
    }

    [Fact]
    public async Task ProductionHistoryQuery_SeeksTheScopedKey_WithoutASortOrSeqScan()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        await scope.ExecuteAsync("set local enable_seqscan = off");
        var plan = await scope.ScalarAsync<string>(
            "explain (format json) " + RequirementSql.SelectVersions,
            ("org_id", world.Org),
            ("project_id", world.Project),
            ("requirement_id", head.RequirementId),
            ("before_version_no", 5),
            ("limit", 50));

        plan.ShouldContain("requirement_version_no_key", Case.Sensitive);
        plan.ShouldNotContain("\"Node Type\": \"Sort\"", Case.Sensitive);
    }

    private static string InsertVersion(RequirementTestWorld world, Guid project, Guid requirement, int number) =>
        "insert into public.requirement_version (id, org_id, project_id, requirement_id, version_no, body, content_hash, source) "
        + $"values (gen_random_uuid(), '{world.Org:D}', '{project:D}', '{requirement:D}', {number}, '', '{Hash}', 'manual')";
}
