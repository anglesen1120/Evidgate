using Evidgate.Modules.Authoring.Application;
using Shouldly;
using Xunit;

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

    /// <summary>The id of the requirement's current head version, the pin every link create must name.</summary>
    internal static Task<Guid> HeadVersionIdAsync(RequirementTestWorld world, Guid requirement) => world.Fixture.ScalarAsync<Guid>(
        world.Database,
        $"""
        select v.id from public.requirement r
        join public.requirement_version v
          on v.org_id = r.org_id and v.requirement_id = r.id and v.version_no = r.head_version_no
        where r.id = '{requirement:D}'
        """);

    internal static async Task<RequirementLinkRow> LinkAsync(
        RequirementTestWorld world, RequirementHead head, Guid? item = null, Guid? manualCase = null)
    {
        var pin = await HeadVersionIdAsync(world, head.RequirementId);
        var result = await world.InScopeAsync((writer, token) => writer.CreateLinkAsync(
            world.Org,
            new RequirementLinkCreate(world.Project, head.RequirementId, item, manualCase, pin, null),
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

    /// <summary>
    /// Waits until a backend is observed blocked behind <paramref name="blockerPid"/>, the open transaction of the
    /// test: the proof that <paramref name="pending"/> overlapped it, and not some other test's wait in the shared
    /// database. Bounded by attempts; a completed task never blocked, so that is reported at once.
    /// </summary>
    internal static async Task WaitUntilBlockedAsync(RequirementTestWorld world, Task pending, int blockerPid)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            pending.IsCompleted.ShouldBeFalse("the call must wait for the transaction that holds what it needs");

            var waiting = await world.Fixture.ScalarAsync<int>(
                world.Database,
                $"select count(*)::int from pg_stat_activity where {blockerPid} = any(pg_blocking_pids(pid))");
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException("the call never blocked behind the open transaction");
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
