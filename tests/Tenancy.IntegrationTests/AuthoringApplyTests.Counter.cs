using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The statement budget of the batched apply (13.1.2 §8 P2/P3/P5): a 200-delete selection is one batch whose
/// statements do not grow with the number of changes.
/// </summary>
public sealed partial class AuthoringApplyTests
{
    /// <summary>The tables whose statements the budget counts: this module's writes and the probes they carry.</summary>
    private static readonly string[] AuthoringTables =
    [
        "public.authoring_change",
        "public.authoring_version",
        "public.checklist_item",
        "public.manual_test_case",
        "public.feature_node",
    ];

    /// <summary>
    /// AC4/AC11/P3: 200 real deletes land in one batch inside the plan's 12-statement bound, which the per-entity path
    /// (four statements per change) cannot meet. The bound is counted over the statements that touch this module's
    /// tables, so permission, tenancy and idempotency reads cannot decide the assertion.
    /// </summary>
    [Fact]
    public async Task Apply_CommandCounter_Bounded()
    {
        await using var world = await StartAsync();
        var ids = await SeedItemsAsync(world, 200);

        var changes = new Guid[ids.Length];
        for (var index = 0; index < ids.Length; index++)
        {
            changes[index] = await QueueDeleteAsync(world, ids[index]);
        }

        var etag = BatchEtag([.. ids.Select(id => (id, 1))]);

        world.Api.Statements.Clear();
        var applied = await ApplyAsync(world, changes, etag);
        var statements = world.Api.Statements.Snapshot();

        applied.Status.ShouldBe(HttpStatusCode.OK);

        var counted = statements.Count(statement =>
            AuthoringTables.Any(table => statement.Contains(table, StringComparison.Ordinal)));

        // At least the two locks, the guard, the delete and the version insert; never the per-entity loop's volume.
        counted.ShouldBeGreaterThanOrEqualTo(4, "the counted statements are the apply's own");
        counted.ShouldBeLessThanOrEqualTo(12, "a 200-change apply stays inside the §8 statement budget");

        var batch = applied.Body.GetProperty("batch_id").GetGuid();
        applied.Body.GetProperty("versions").GetArrayLength().ShouldBe(ids.Length);
        (await CountAsync(world, "select count(*) from public.authoring_version where batch_id = @batch", ("batch", batch)))
            .ShouldBe((long)ids.Length);
        (await CountAsync(world, "select count(*) from public.checklist_item where id = any(@ids) and deleted_at is not null", ("ids", ids)))
            .ShouldBe((long)ids.Length);
        (await CountAsync(
            world,
            "select count(*) from public.authoring_change where org_id = @org and state = 'applied' and applied_batch_id = @batch",
            ("org", world.Org),
            ("batch", batch))).ShouldBe((long)ids.Length);
        (await AuditCountAsync(world, "authoring.change.apply")).ShouldBe(1);
    }

    /// <summary>Seeds <paramref name="count"/> live checklist items in one statement and answers their ids.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="count">How many items to seed.</param>
    private static async Task<Guid[]> SeedItemsAsync(World world, int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()).ToArray();

        await ExecuteAsync(
            world,
            """
            insert into public.checklist_item
                   (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            select item, @org, @project, @node, 'Seeded ' || item::text, 'high', 'active', 'manual', 0
              from unnest(@ids::uuid[]) as item
            """,
            ("ids", ids),
            ("org", world.Org),
            ("project", world.Project),
            ("node", world.Category));

        return ids;
    }
}
