using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The coverage view: path folding, distinct counts, stale pins, availability, paging, scope and query plans.</summary>
[Trait("Category", "Docker")]
public sealed class RequirementCoverageTests
{
    private const string Unavailable = "unavailable";

    private readonly PostgresTenancyFixture _fixture;

    public RequirementCoverageTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ItemOnlyLink_ListsItsCasesViaItem_AndNothingForASameTitledRequirementOrAnUnrelatedItem()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var r1 = await world.CreatedAsync(world.Create("user_story", "Same title"));
        var r2 = await world.CreatedAsync(world.Create("user_story", "Same title"));
        var targets = await RequirementLinkTestData.SeedAsync(world);
        await RequirementLinkTestData.SeedAsync(world, firstNumber: 10);
        await RequirementLinkTestData.LinkAsync(world, r1, targets.Item);

        var first = await RequirementLinkTestData.CoverageAsync(world, r1.RequirementId);
        var second = await RequirementLinkTestData.CoverageAsync(world, r2.RequirementId);

        first.LinkedCaseCount.ShouldBe(2);
        first.ApprovedCaseCount.ShouldBe(1);
        first.LinkCount.ShouldBe(1);
        first.StaleLinkCount.ShouldBe(0);
        first.Cases.Select(c => c.Code).ShouldBe(["LINK-001", "LINK-002"]);
        first.Cases.ShouldAllBe(c => c.LinkPath == "via_item" && c.ResultAvailability == Unavailable);
        first.Cases[0].State.ShouldBe("approved");
        first.Cases[0].AutomatedCaseIds.AsEnumerable().ShouldBe([targets.AutomatedCase]);
        first.Cases[1].AutomatedCaseIds.ShouldBeEmpty();
        first.ResultSourceAvailable.ShouldBeFalse();

        second.LinkedCaseCount.ShouldBe(0);
        second.LinkCount.ShouldBe(0);
        second.Cases.ShouldBeEmpty();
        second.Targets.ShouldBeEmpty();
    }

    [Fact]
    public async Task DirectLinkToACaseOfTheLinkedItem_ReportsBoth_AndStillCountsTheCaseAndItsAutomatedIdentityOnce()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);
        await RequirementLinkTestData.LinkAsync(world, head, targets.Item);
        await RequirementLinkTestData.LinkAsync(world, head, manualCase: targets.ApprovedCase);

        var coverage = await RequirementLinkTestData.CoverageAsync(world, head.RequirementId);

        coverage.LinkCount.ShouldBe(2);
        coverage.LinkedCaseCount.ShouldBe(2);
        coverage.ApprovedCaseCount.ShouldBe(1);
        coverage.Cases.Count.ShouldBe(2);
        coverage.Cases.Single(c => c.CaseId == targets.ApprovedCase).LinkPath.ShouldBe("both");
        coverage.Cases.Single(c => c.CaseId == targets.ApprovedCase).AutomatedCaseIds.Count.ShouldBe(1);
        coverage.Cases.Single(c => c.CaseId == targets.DraftCase).LinkPath.ShouldBe("via_item");
        coverage.Targets.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ADirectLinkAlone_ListsOnlyThatCaseAsDirect()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);
        await RequirementLinkTestData.LinkAsync(world, head, manualCase: targets.DraftCase);

        var coverage = await RequirementLinkTestData.CoverageAsync(world, head.RequirementId);

        coverage.Cases.Select(c => (c.CaseId, c.LinkPath)).ShouldBe([(targets.DraftCase, "direct")]);
        coverage.ApprovedCaseCount.ShouldBe(0);
    }

    [Fact]
    public async Task AfterANewVersion_OlderPinsAreCountedStale_AndEachTargetShowsItsOwnVersion()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);
        await RequirementLinkTestData.LinkAsync(world, head, targets.Item);
        var appended = await world.InScopeAsync((writer, token) => writer.AppendVersionAsync(
            world.Org, world.Project, head.RequirementId, head.RowVersion, "second wording", "manual", null, null, token));
        await RequirementLinkTestData.LinkAsync(world, head, manualCase: targets.DraftCase);

        var coverage = await RequirementLinkTestData.CoverageAsync(world, head.RequirementId);

        coverage.HeadVersionId.ShouldBe(appended.Value.VersionId);
        coverage.HeadVersionNo.ShouldBe(2);
        coverage.StaleLinkCount.ShouldBe(1);
        coverage.Targets.Single(t => t.ChecklistItemId == targets.Item).ShouldSatisfyAllConditions(
            t => t.Stale.ShouldBeTrue(),
            t => t.VersionNo.ShouldBe(1),
            t => t.VersionId.ShouldBe(head.VersionId));
        coverage.Targets.Single(t => t.ManualTestCaseId == targets.DraftCase).ShouldSatisfyAllConditions(
            t => t.Stale.ShouldBeFalse(),
            t => t.VersionNo.ShouldBe(2),
            t => t.VersionId.ShouldBe(appended.Value.VersionId));
    }

    [Fact]
    public async Task SoftDeletedCases_AreNotCovered_AndAnUnlinkedTargetDisappears()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);
        var link = await RequirementLinkTestData.LinkAsync(world, head, targets.Item);
        await _fixture.ExecuteAsync(world.Database, $"update public.manual_test_case set deleted_at = now() where id = '{targets.DraftCase:D}'");

        var live = await RequirementLinkTestData.CoverageAsync(world, head.RequirementId);
        await world.InScopeAsync((writer, token) => writer.RemoveLinkAsync(world.Org, world.Project, head.RequirementId, link.LinkId, token));
        var removed = await RequirementLinkTestData.CoverageAsync(world, head.RequirementId);

        live.Cases.Select(c => c.CaseId).ShouldBe([targets.ApprovedCase]);
        live.LinkedCaseCount.ShouldBe(1);
        removed.Cases.ShouldBeEmpty();
        removed.LinkCount.ShouldBe(0);
    }

    [Fact]
    public async Task Paging_ByCode_HasNoGapOrRepeat_KeepsTheCountsOfTheWholeSet_AndEndsWithoutACursor()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);
        await RequirementLinkTestData.LinkAsync(world, head, targets.Item);

        var first = await RequirementLinkTestData.CoverageAsync(world, head.RequirementId, limit: 1);
        var second = await RequirementLinkTestData.CoverageAsync(world, head.RequirementId, limit: 1, afterCode: first.NextCode);

        first.Cases.Select(c => c.Code).ShouldBe(["LINK-001"]);
        first.NextCode.ShouldBe("LINK-001");
        second.Cases.Select(c => c.Code).ShouldBe(["LINK-002"]);
        second.NextCode.ShouldBeNull();
        first.LinkedCaseCount.ShouldBe(2);
        second.LinkedCaseCount.ShouldBe(2);
    }

    [Fact]
    public async Task Coverage_RefusesABadPageSize_AndAnUnknownForeignProjectOrForeignTenantRequirement()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        await using var other = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());

        var zero = await world.InScopeAsync((writer, token) => writer.GetCoverageAsync(world.Org, world.Project, head.RequirementId, 0, null, token), commit: false);
        var huge = await world.InScopeAsync((writer, token) => writer.GetCoverageAsync(world.Org, world.Project, head.RequirementId, 201, null, token), commit: false);
        var unknown = await world.InScopeAsync((writer, token) => writer.GetCoverageAsync(world.Org, world.Project, Guid.CreateVersion7(), 10, null, token), commit: false);
        var sibling = await world.InScopeAsync((writer, token) => writer.GetCoverageAsync(world.Org, world.OtherProject, head.RequirementId, 10, null, token), world.OtherProject, commit: false);
        var crossTenant = await other.InScopeAsync((writer, token) => writer.GetCoverageAsync(other.Org, other.Project, head.RequirementId, 10, null, token), commit: false);

        zero.Error!.Status.ShouldBe(422);
        huge.Error!.Status.ShouldBe(422);
        unknown.Error!.Status.ShouldBe(404);
        sibling.Error!.Status.ShouldBe(404);
        crossTenant.Error!.Status.ShouldBe(404);
        (await RequirementLinkTestData.CoverageAsync(world, head.RequirementId)).Cases.ShouldBeEmpty();
    }

    [Fact]
    public async Task CoverageStatement_UsesIndexes_WithoutASeqScan()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);
        await RequirementLinkTestData.LinkAsync(world, head, targets.Item);
        await RequirementLinkTestData.LinkAsync(world, head, manualCase: targets.ApprovedCase);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        await scope.ExecuteAsync("set local enable_seqscan = off");
        var plan = await scope.ScalarAsync<string>(
            "explain (format json) " + RequirementCoverageSql.Coverage,
            ("org_id", world.Org),
            ("project_id", world.Project),
            ("requirement_id", head.RequirementId),
            ("after_code", "LINK-000"),
            ("take_targets", 51),
            ("take_cases", 51));

        plan.ShouldContain("requirement_link_", Case.Sensitive);
        plan.ShouldNotContain("\"Node Type\": \"Seq Scan\"", Case.Sensitive);
    }

    [Fact]
    public async Task SoftDeletedChecklistItem_ReachesNoCase_WhileItsLinkStillCountsAndIsListed()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var targets = await RequirementLinkTestData.SeedAsync(world);
        await RequirementLinkTestData.LinkAsync(world, head, targets.Item);
        var live = await RequirementLinkTestData.CoverageAsync(world, head.RequirementId);
        await _fixture.ExecuteAsync(world.Database, $"update public.checklist_item set deleted_at = now() where id = '{targets.Item:D}'");

        var deleted = await RequirementLinkTestData.CoverageAsync(world, head.RequirementId);

        live.LinkedCaseCount.ShouldBe(2);
        live.Cases.Count.ShouldBe(2);
        deleted.LinkedCaseCount.ShouldBe(0);
        deleted.ApprovedCaseCount.ShouldBe(0);
        deleted.Cases.ShouldBeEmpty();
        deleted.LinkCount.ShouldBe(1);
        deleted.Targets.Select(t => t.ChecklistItemId).ShouldBe([targets.Item]);
    }
}
