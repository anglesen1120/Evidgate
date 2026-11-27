using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The link writer: pin at the head, no-op repeat, refusals, removal, tenant scope and contention.</summary>
[Trait("Category", "Docker")]
public sealed class RequirementLinkWriterTests
{
    private readonly PostgresTenancyFixture _fixture;

    public RequirementLinkWriterTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Create_PinsTheHeadAtTheTime_AndALaterAppendDoesNotMoveThePin()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);

        var first = await RequirementLinkTestData.LinkAsync(world, head, targets.Item);
        var next = await AppendAsync(world, head);
        var second = await RequirementLinkTestData.LinkAsync(world, head, manualCase: targets.DraftCase);

        first.Changed.ShouldBeTrue();
        first.VersionId.ShouldBe(head.VersionId);
        first.VersionNo.ShouldBe(1);
        second.VersionId.ShouldBe(next.VersionId);
        second.VersionNo.ShouldBe(2);
        (await PinnedVersionAsync(world, first.LinkId)).ShouldBe(head.VersionId);
    }

    [Fact]
    public async Task Create_RepeatAtTheHead_IsANoOp_AndAtAnOlderPin_Is409WithoutMovingIt()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);
        var first = await RequirementLinkTestData.LinkAsync(world, head, targets.Item);

        var repeat = await RequirementLinkTestData.LinkAsync(world, head, targets.Item);
        await AppendAsync(world, head);
        var older = await world.InScopeAsync((writer, token) => writer.CreateLinkAsync(
            world.Org, new RequirementLinkCreate(world.Project, head.RequirementId, targets.Item, null, null, null), token));

        repeat.Changed.ShouldBeFalse();
        repeat.LinkId.ShouldBe(first.LinkId);
        older.Error!.Status.ShouldBe(409);
        (await PinnedVersionAsync(world, first.LinkId)).ShouldBe(head.VersionId);
        (await LinkCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Create_RefusesBadTargetsAndRequirements_WritesNothing_AndAcceptsTheValidRequest()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var foreign = await world.CreatedAsync(world.Create(project: world.OtherProject));
        var own = await RequirementLinkTestData.SeedAsync(world);
        var other = await RequirementLinkTestData.SeedAsync(world, world.OtherProject);
        await _fixture.ExecuteAsync(world.Database, $"update public.manual_test_case set deleted_at = now() where id = '{own.DraftCase:D}'");
        var deleted = await world.CreatedAsync(world.Create());
        await _fixture.ExecuteAsync(world.Database, $"update public.requirement set deleted_at = now() where id = '{deleted.RequirementId:D}'");

        var none = await TryAsync(world, head.RequirementId, null, null);
        var both = await TryAsync(world, head.RequirementId, own.Item, own.ApprovedCase);
        var foreignItem = await TryAsync(world, head.RequirementId, other.Item, null);
        var foreignCase = await TryAsync(world, head.RequirementId, null, other.ApprovedCase);
        var deletedCase = await TryAsync(world, head.RequirementId, null, own.DraftCase);
        var missing = await TryAsync(world, Guid.CreateVersion7(), own.Item, null);
        var foreignRequirement = await TryAsync(world, foreign.RequirementId, own.Item, null);
        var deletedRequirement = await TryAsync(world, deleted.RequirementId, own.Item, null);
        var moved = await world.InScopeAsync((writer, token) => writer.CreateLinkAsync(
            world.Org, new RequirementLinkCreate(world.Project, head.RequirementId, own.Item, null, Guid.CreateVersion7(), null), token));

        none.Error!.Details!.ShouldContain(detail => detail.Path == "/target");
        both.Error!.Details!.ShouldContain(detail => detail.Path == "/target");
        foreignItem.Error!.Details!.ShouldContain(detail => detail.Path == "/checklist_item_id");
        foreignCase.Error!.Details!.ShouldContain(detail => detail.Path == "/manual_test_case_id");
        deletedCase.Error!.Details!.ShouldContain(detail => detail.Path == "/manual_test_case_id");
        missing.Error!.Status.ShouldBe(404);
        foreignRequirement.Error!.Status.ShouldBe(404);
        deletedRequirement.Error!.Status.ShouldBe(404);
        moved.Error!.Status.ShouldBe(412);
        (await LinkCountAsync(world)).ShouldBe(0);

        var accepted = await world.InScopeAsync((writer, token) => writer.CreateLinkAsync(
            world.Org, new RequirementLinkCreate(world.Project, head.RequirementId, own.Item, null, head.VersionId, null), token));
        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        (await LinkCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Create_ByAnotherTenant_IsNotFound_AndTheOwnerOfTheRequirementCanLink()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        await using var other = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);
        var theirs = await RequirementLinkTestData.SeedAsync(other);

        var crossTenant = await other.InScopeAsync((writer, token) => writer.CreateLinkAsync(
            other.Org, new RequirementLinkCreate(other.Project, head.RequirementId, theirs.Item, null, null, null), token));
        var own = await RequirementLinkTestData.LinkAsync(world, head, targets.Item);

        crossTenant.Error!.Status.ShouldBe(404);
        own.Changed.ShouldBeTrue();
        (await LinkCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Remove_DeletesTheLink_AndAnUnknownWrongRequirementOrForeignProjectLinkIs404()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var sibling = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);
        var link = await RequirementLinkTestData.LinkAsync(world, head, targets.Item);

        var wrongRequirement = await world.InScopeAsync((writer, token) => writer.RemoveLinkAsync(world.Org, world.Project, sibling.RequirementId, link.LinkId, token));
        var foreignProject = await world.InScopeAsync((writer, token) => writer.RemoveLinkAsync(world.Org, world.OtherProject, head.RequirementId, link.LinkId, token), world.OtherProject);
        var unknown = await world.InScopeAsync((writer, token) => writer.RemoveLinkAsync(world.Org, world.Project, head.RequirementId, Guid.CreateVersion7(), token));
        (await LinkCountAsync(world)).ShouldBe(1);

        var removed = await world.InScopeAsync((writer, token) => writer.RemoveLinkAsync(world.Org, world.Project, head.RequirementId, link.LinkId, token));
        var again = await world.InScopeAsync((writer, token) => writer.RemoveLinkAsync(world.Org, world.Project, head.RequirementId, link.LinkId, token));

        wrongRequirement.Error!.Status.ShouldBe(404);
        foreignProject.Error!.Status.ShouldBe(404);
        unknown.Error!.Status.ShouldBe(404);
        removed.Value.ShouldBe(link.LinkId);
        again.Error!.Status.ShouldBe(404);
        (await LinkCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_WhileAnotherWriterHoldsTheRequirementRow_Is503_AndSucceedsOnceItIsReleased()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);

        await using (var holder = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org))
        {
            await holder.ScalarAsync<Guid>("select id from public.requirement where id = @id for update", ("id", head.RequirementId));

            var blocked = await world.InScopeAsync((writer, token) => writer.CreateLinkAsync(
                world.Org, new RequirementLinkCreate(world.Project, head.RequirementId, targets.Item, null, null, null), token));

            blocked.Error!.Status.ShouldBe(503);
        }

        (await LinkCountAsync(world)).ShouldBe(0);
        (await RequirementLinkTestData.LinkAsync(world, head, targets.Item)).Changed.ShouldBeTrue();
    }

    private static Task<Result<RequirementLinkRow>> TryAsync(
        RequirementTestWorld world, Guid requirement, Guid? item, Guid? manualCase) =>
        world.InScopeAsync((writer, token) => writer.CreateLinkAsync(
            world.Org, new RequirementLinkCreate(world.Project, requirement, item, manualCase, null, null), token));

    private static async Task<RequirementHead> AppendAsync(RequirementTestWorld world, RequirementHead head)
    {
        var result = await world.InScopeAsync((writer, token) => writer.AppendVersionAsync(
            world.Org, world.Project, head.RequirementId, head.RowVersion, "second wording", "manual", null, null, token));
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.Value;
    }

    private Task<Guid> PinnedVersionAsync(RequirementTestWorld world, Guid link) =>
        _fixture.ScalarAsync<Guid>(world.Database, $"select requirement_version_id from public.requirement_link where id = '{link:D}'");

    private Task<int> LinkCountAsync(RequirementTestWorld world) =>
        _fixture.ScalarAsync<int>(world.Database, $"select count(*)::int from public.requirement_link where org_id = '{world.Org:D}'");
}
