using Evidgate.Modules.Authoring.Application;
using Shouldly;

namespace Tenancy.IntegrationTests;

internal sealed record RequirementLinkTargets(Guid Item, Guid ApprovedCase, Guid DraftCase, Guid AutomatedCase);

internal static class RequirementLinkTestData
{
    internal static async Task<RequirementLinkTargets> SeedAsync(RequirementTestWorld world, Guid? project = null, int firstNumber = 1)
    {
        var node = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();
        var tree = new AuthoringLinkTree(world.Org, project ?? world.Project, item);
        await world.Fixture.ExecuteAsync(world.Database, $"""
            insert into public.feature_node (id, org_id, project_id, kind, title, position)
            values ('{node}', '{world.Org}', '{tree.Project}', 'module', 'Coverage {firstNumber}', {firstNumber});
            insert into public.checklist_item
                (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            values ('{item}', '{world.Org}', '{tree.Project}', '{node}', 'Coverage {firstNumber}', 'high', 'active', 'manual', 0);
            """);
        var approved = await AuthoringLinkTestData.SeedManualCaseAsync(world.Fixture, world.Database, tree, firstNumber);
        var draft = await AuthoringLinkTestData.SeedManualCaseAsync(world.Fixture, world.Database, tree, firstNumber + 1);
        var automated = await AuthoringLinkTestData.SeedCatalogCaseAsync(world.Fixture, world.Database, tree);
        await world.Fixture.ExecuteAsync(world.Database, $"""
            update public.manual_test_case set state = 'approved' where id = '{approved}';
            insert into public.manual_case_link (id, org_id, project_id, manual_test_case_id, test_case_id)
            values ('{Guid.CreateVersion7()}', '{world.Org}', '{tree.Project}', '{approved}', '{automated}');
            """);
        return new RequirementLinkTargets(item, approved, draft, automated);
    }

    internal static async Task<RequirementLinkRow> LinkAsync(
        RequirementTestWorld world, RequirementHead head, Guid? item = null, Guid? manualCase = null)
    {
        var result = await world.InScopeAsync((writer, token) => writer.CreateLinkAsync(
            world.Org,
            new RequirementLinkCreate(world.Project, head.RequirementId, item, manualCase, null, null),
            token));
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.Value;
    }

    internal static async Task<RequirementCoverage> CoverageAsync(
        RequirementTestWorld world, Guid requirement, int limit = 50, string? afterCode = null)
    {
        var result = await world.InScopeAsync((writer, token) => writer.GetCoverageAsync(
            world.Org, world.Project, requirement, limit, afterCode, token), commit: false);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.Value;
    }

    internal static string InsertSql(
        RequirementTestWorld world, Guid requirement, Guid version, Guid? item, Guid? manualCase, Guid? project = null) => $"""
        insert into public.requirement_link
            (id, org_id, project_id, requirement_id, requirement_version_id, checklist_item_id, manual_test_case_id)
        values ('{Guid.CreateVersion7()}', '{world.Org}', '{project ?? world.Project}', '{requirement}', '{version}',
                {Uuid(item)}, {Uuid(manualCase)})
        """;

    private static string Uuid(Guid? id) => id is { } value ? $"'{value:D}'" : "null";
}
