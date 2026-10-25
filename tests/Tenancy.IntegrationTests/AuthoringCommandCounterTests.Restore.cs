using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T3's statement budget: a restore is a bounded number of statements, one of them the sibling group's advisory
/// lock (hand-over (a), OI-13-128) — never a per-sibling loop and never a second append path. The create side of the
/// counter lives in <c>AuthoringCommandCounterTests.cs</c>.
/// </summary>
/// <remarks>
/// The count is taken from the host's Npgsql command log (<c>TenancyApiFactory.Statements</c>), so it measures what the
/// database was actually asked to do through the real pipeline — tenant resolution, the permission gate, the command and
/// the writer — and not what one layer believes it issues.
/// </remarks>
public sealed partial class AuthoringCommandCounterTests
{
    /// <summary>
    /// One restore of each route — <c>POST …/nodes/{n}/restore</c> (6), <c>…/checklist-items/{i}/restore</c> (11),
    /// <c>…/manual-cases/{m}/restore</c> (18) and <c>…/versions/{v}/restore</c> (24) — is a bounded read side, one entity
    /// update, exactly one version append, one audit row, one idempotency claim and exactly one group lock.
    /// </summary>
    /// <param name="scenario">Which route the case drives, and so which entity kind it restores.</param>
    [Theory]
    [InlineData("node")]
    [InlineData("item")]
    [InlineData("case")]
    [InlineData("version")]
    public async Task Restore_StaysWithinBudget(string scenario)
    {
        await using var world = await StartAsync();

        var authoring = $"/v1/projects/{world.Project}/authoring";

        // The ladder of AC1: a module, its feature, its category, and the item every scenario below hangs its target on.
        var module = await CreateNodeAsync(world, authoring, parent: null, kind: "module", title: "Checkout");
        var feature = await CreateNodeAsync(world, authoring, parent: module, kind: "feature", title: "Payment");
        var category = await CreateNodeAsync(world, authoring, parent: feature, kind: "category", title: "Cards");
        var item = await CreateItemAsync(world, authoring, category, "Pay online");

        // The entity the route restores, produced by the routes that own it: the create (version 1) and the soft delete
        // (version 2) of one node, item or case.
        var (entityId, collection, entityTable) = scenario switch
        {
            "node" => (
                await CreateNodeAsync(world, authoring, category, "sub_category", "Refunds"),
                "nodes",
                "feature_node"),
            "item" => (item, "checklist-items", "checklist_item"),
            "case" => (
                await CreateCaseAsync(world, authoring, item, "Card is charged"),
                "manual-cases",
                "manual_test_case"),
            _ => (
                await CreateCaseAsync(world, authoring, item, "Card is declined"),
                "manual-cases",
                "manual_test_case"),
        };

        var entityUrl = $"{authoring}/{collection}/{entityId:D}";
        var deleted = await world.Client.SendAsync(
            HttpMethod.Delete, entityUrl, world.As, idempotencyKey: NewKey(), ifMatch: "\"1\"");
        deleted.Status.ShouldBe(HttpStatusCode.NoContent, deleted.ErrorMessage);

        // Route 24 names a version instead of presenting a tag; routes 6/11/18 present the tombstone's version.
        string? ifMatch = scenario == "version" ? null : "\"2\"";
        var url = scenario == "version"
            ? $"{world.Versions}/{await VersionIdAsync(world, entityId):D}/restore"
            : $"{entityUrl}/restore";

        world.Api.Statements.Clear();

        var restored = await world.Client.SendAsync(HttpMethod.Post, url, world.As, idempotencyKey: NewKey(), ifMatch: ifMatch);

        restored.Status.ShouldBe(HttpStatusCode.OK, restored.ErrorMessage);
        restored.Body.GetProperty("version").GetInt32().ShouldBe(3);

        var statements = world.Api.Statements.Snapshot();

        // The module's own statements, as the catalog budget test counts them (CatalogSuiteTests.cs:700): the pipeline's
        // tenant and permission reads stay in, so a hidden N+1 in either shows up here too. A node's restore adds the
        // AC2 group count read (one statement for the whole group, AuthoringSql.NodeGroupSummary) to this total.
        var writes = statements.Where(statement => statement.Contains("public.", StringComparison.Ordinal)).ToList();
        writes.Count.ShouldBeLessThanOrEqualTo(20, string.Join(Environment.NewLine, statements));

        // One append path (AC8), one entity write of the kind this route restores, one audit row (AC18), one claim —
        // never one per sibling.
        statements.Count(statement => statement.Contains("insert into public.authoring_version", StringComparison.Ordinal))
            .ShouldBe(1, string.Join(Environment.NewLine, statements));
        statements.Count(statement => statement.Contains("update public." + entityTable, StringComparison.Ordinal))
            .ShouldBe(1, string.Join(Environment.NewLine, statements));
        statements.Count(statement => statement.Contains("insert into public.audit_log", StringComparison.Ordinal))
            .ShouldBe(1, string.Join(Environment.NewLine, statements));
        statements.Count(statement => statement.Contains("insert into public.idempotency_record", StringComparison.Ordinal))
            .ShouldBe(1, string.Join(Environment.NewLine, statements));

        // Hand-over (a): the sibling group's advisory lock is taken exactly once — a node's parent group, an item's group
        // under its node, a case's group under its item. The count is the group lock's own statement
        // (`AuthoringSql.LockSiblingGroup`, the `hashtextextended` key), because the audit chain takes its own advisory
        // lock on a different key (`AuditTrail.AdvisoryLock`, `hashtext('audit:'…)`) with every audit row, and counting
        // both would say nothing about the per-sibling loop this test forbids.
        statements.Count(statement => statement.Contains("pg_advisory_xact_lock(hashtextextended", StringComparison.Ordinal))
            .ShouldBe(1, string.Join(Environment.NewLine, statements));
        statements.Count(statement => statement.Contains("pg_advisory_xact_lock(hashtext('audit:'", StringComparison.Ordinal))
            .ShouldBe(1, string.Join(Environment.NewLine, statements));
    }

    /// <summary>The id of an entity's version 1, read where the timeline stores it (no route publishes it).</summary>
    private async Task<Guid> VersionIdAsync(World world, Guid entityId) =>
        await Fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.authoring_version where entity_id = '{entityId:D}' and version_no = 1");

    /// <summary>Route 2's create, answered with the new node id.</summary>
    private static async Task<Guid> CreateNodeAsync(
        World world,
        string authoring,
        Guid? parent,
        string kind,
        string title)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post, $"{authoring}/nodes", world.As, new { parent_id = parent, kind, title }, NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("node_id");
    }

    /// <summary>Route 8's create, answered with the new item id.</summary>
    private static async Task<Guid> CreateItemAsync(World world, string authoring, Guid node, string title)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post,
            $"{authoring}/checklist-items",
            world.As,
            new { node_id = node, title, priority = "high" },
            NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("item_id");
    }

    /// <summary>Route 13's create, answered with the new case id.</summary>
    private static async Task<Guid> CreateCaseAsync(World world, string authoring, Guid item, string title)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post,
            $"{authoring}/manual-cases",
            world.As,
            new
            {
                checklist_item_id = item,
                title,
                steps = new[] { new { no = 1, action = "Open the page", expected = "The page is shown" } },
                priority = "high",
            },
            NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("case_id");
    }
}
