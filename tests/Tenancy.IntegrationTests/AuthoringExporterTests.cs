using System.Data.Common;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Import;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T5 against a real PostgreSQL: <c>AuthoringExporter</c> is the read half of route 25 (AC14), so these cases pin
/// the file it produces — the row order, the deterministic keys, the subtree and <c>include</c> scoping, the exclusion of
/// tombstones, the constant statement count (P3), the 5 000-row cap (AC16) and the AC10 answers — through the module's
/// own tenant scope on the app role, never a stub.
/// </summary>
/// <remarks>
/// The data is seeded as the owner (org, project, tree, items, cases) so a case can pin an exact tree — including an
/// insertion order that disagrees with the stored <c>position</c> order, which is what proves the export's order comes
/// from the statements and not from the table's heap. The export itself runs on the app role's read-only tenant scope
/// (<c>ITenantScopeFactory.BeginAsync(readOnlyTransaction: true)</c>), so row level security and the NFC-stored text are
/// the production ones.
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuthoringExporterTests
{
    /// <summary>AC14's steps documents, written in <c>jsonb</c>'s own key order (length, then bytewise), so the compact
    /// export of each one is the literal itself.</summary>
    private const string CardOkSteps = """{"steps":[{"no":1,"action":"Tap card","expected":"Payment accepted"}]}""";

    /// <summary>The second case's document (a declined card).</summary>
    private const string CardDeclinedSteps = """{"steps":[{"no":1,"action":"Tap card","expected":"A decline is shown"}]}""";

    /// <summary>The third case's document (a retry).</summary>
    private const string RetrySteps = """{"steps":[{"no":1,"action":"Retry","expected":"Payment accepted"}]}""";

    private readonly PostgresTenancyFixture _fixture;

    public AuthoringExporterTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ the standard tree

    /// <summary>The ids of one seeded tree; the names are the titles' own words.</summary>
    /// <param name="Shop">Root module "Shop" (position 0).</param>
    /// <param name="Orders">Root module "Orders" (position 1).</param>
    /// <param name="Checkout">Feature "Checkout" under the shop (position 0).</param>
    /// <param name="Cart">Feature "Cart" under the shop (position 1).</param>
    /// <param name="Payment">Category "Payment" under the checkout.</param>
    /// <param name="Declined">Sub-category "Declined" under the payment.</param>
    /// <param name="CardItem">Item "Pay with a card" (position 0 of the payment).</param>
    /// <param name="CashItem">Item "Pay with cash" (position 1 of the payment).</param>
    /// <param name="RetryItem">Item "Retry after decline" (the declined sub-category).</param>
    /// <param name="CardOk">Case SHOP-001.</param>
    /// <param name="CardDeclined">Case SHOP-002.</param>
    /// <param name="RetryCase">Case SHOP-003.</param>
    private sealed record Tree(
        Guid Shop,
        Guid Orders,
        Guid Checkout,
        Guid Cart,
        Guid Payment,
        Guid Declined,
        Guid CardItem,
        Guid CashItem,
        Guid RetryItem,
        Guid CardOk,
        Guid CardDeclined,
        Guid RetryCase);

    private static Tree NewTree() => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7());

    // ------------------------------------------------------------------ the world

    private sealed record World(
        string Database,
        Guid Org,
        Guid Project,
        NpgsqlDataSource DataSource,
        ServiceProvider Provider) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await DataSource.DisposeAsync();
        }
    }

    private sealed record ExportAttempt(Result<IReadOnlyList<ImportRow>> Result, int Statements);

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");

        var dataSource = NpgsqlDataSource.Create(_fixture.AppConnectionString(database));
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddEvidgateTenancy();

        return new World(database, org, project, dataSource, services.BuildServiceProvider());
    }

    private async Task<(Guid Org, Guid Project)> SeedTenantAsync(string database, string orgSlug, string projectSlug)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();

        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await seed.ExecuteAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            ("id", org),
            ("name", orgSlug),
            ("slug", orgSlug));
        await seed.ExecuteAsync(
            "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
            ("id", project),
            ("org_id", org),
            ("name", projectSlug),
            ("slug", projectSlug));
        await seed.CommitAsync();

        return (org, project);
    }

    /// <summary>Seeds one more project of an existing tenant, as the owner, so a case can address a sibling project.</summary>
    /// <param name="database">The seeded database.</param>
    /// <param name="orgId">The tenant the project belongs to.</param>
    /// <param name="projectSlug">The project's slug (unique per tenant, which also fixes its case prefix).</param>
    private async Task<Guid> SeedProjectAsync(string database, Guid orgId, string projectSlug)
    {
        var project = Guid.CreateVersion7();

        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await seed.ExecuteAsync(
            "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
            ("id", project),
            ("org_id", orgId),
            ("name", projectSlug),
            ("slug", projectSlug));
        await seed.CommitAsync();

        return project;
    }

    /// <summary>
    /// Runs one export on a read-only app-role tenant scope. <paramref name="orgId"/>/<paramref name="projectId"/> exist so
    /// the AC10 cases can address another tenant or another project of the same tenant; the statements are counted through
    /// a wrapper that only delegates, so the number is exactly what <see cref="AuthoringExporter"/> issued.
    /// </summary>
    private static async Task<ExportAttempt> ExportAsync(
        World world,
        Guid? nodeId = null,
        ImportInclude include = ImportInclude.All,
        Guid? orgId = null,
        Guid? projectId = null)
    {
        await using var services = world.Provider.CreateAsyncScope();
        var org = orgId ?? world.Org;
        var project = projectId ?? world.Project;

        var factory = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await factory.BeginAsync(org, project, true, TestContext.Current.CancellationToken);

        var counting = new CountingTenantDb(services.ServiceProvider.GetRequiredService<ITenantDb>());
        var result = await new AuthoringExporter(counting).ExportAsync(
            org,
            project,
            nodeId,
            include,
            TestContext.Current.CancellationToken);

        return new ExportAttempt(result, counting.Statements);
    }

    // ------------------------------------------------------------------ the seeded tree

    /// <summary>
    /// Seeds the standard tree as the owner. The rows are inserted in an order that disagrees with their stored positions
    /// (the second root first, the item at position 1 before the one at position 0, SHOP-002 before SHOP-001), so every
    /// order assertion below is decided by the export's statements rather than by the insertion order.
    /// </summary>
    private async Task<Tree> SeedTreeAsync(World world)
    {
        var tree = NewTree();

        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));

        await seed.ExecuteAsync($$"""
            insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position) values
              ('{{tree.Shop:D}}', '{{world.Org:D}}', '{{world.Project:D}}', null, 'module', 'Shop', 0),
              ('{{tree.Orders:D}}', '{{world.Org:D}}', '{{world.Project:D}}', null, 'module', 'Orders', 1),
              ('{{tree.Cart:D}}', '{{world.Org:D}}', '{{world.Project:D}}', '{{tree.Shop:D}}', 'feature', 'Cart', 1),
              ('{{tree.Checkout:D}}', '{{world.Org:D}}', '{{world.Project:D}}', '{{tree.Shop:D}}', 'feature', 'Checkout', 0),
              ('{{tree.Payment:D}}', '{{world.Org:D}}', '{{world.Project:D}}', '{{tree.Checkout:D}}', 'category', 'Payment', 0),
              ('{{tree.Declined:D}}', '{{world.Org:D}}', '{{world.Project:D}}', '{{tree.Payment:D}}', 'sub_category', 'Declined', 0);

            insert into public.checklist_item
              (id, org_id, project_id, feature_node_id, title, description, priority, state, source, position) values
              ('{{tree.CashItem:D}}', '{{world.Org:D}}', '{{world.Project:D}}', '{{tree.Payment:D}}', 'Pay with cash', 'Cash on delivery', 'low', 'draft', 'manual', 1),
              ('{{tree.CardItem:D}}', '{{world.Org:D}}', '{{world.Project:D}}', '{{tree.Payment:D}}', 'Pay with a card', 'Visa or Mastercard', 'critical', 'active', 'manual', 0),
              ('{{tree.RetryItem:D}}', '{{world.Org:D}}', '{{world.Project:D}}', '{{tree.Declined:D}}', 'Retry after decline', null, 'medium', 'draft', 'manual', 0);

            insert into public.manual_test_case
              (id, org_id, project_id, checklist_item_id, code, title, description, precondition, steps, priority, state, source) values
              ('{{tree.CardDeclined:D}}', '{{world.Org:D}}', '{{world.Project:D}}', '{{tree.CardItem:D}}', 'SHOP-002', 'Card declined', null, 'A card is declined', '{{CardDeclinedSteps}}'::jsonb, 'high', 'approved', 'manual'),
              ('{{tree.CardOk:D}}', '{{world.Org:D}}', '{{world.Project:D}}', '{{tree.CardItem:D}}', 'SHOP-001', 'Card accepted', 'Happy path', null, '{{CardOkSteps}}'::jsonb, 'critical', 'approved', 'manual'),
              ('{{tree.RetryCase:D}}', '{{world.Org:D}}', '{{world.Project:D}}', '{{tree.RetryItem:D}}', 'SHOP-003', 'Retry after decline', null, null, '{{RetrySteps}}'::jsonb, 'medium', 'draft', 'manual');
            """);

        await seed.CommitAsync();

        return tree;
    }

    /// <summary>Executes one statement batch as the owner and commits it.</summary>
    private async Task ExecuteAsync(World world, string sql)
    {
        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        await seed.ExecuteAsync(sql);
        await seed.CommitAsync();
    }

    // ------------------------------------------------------------------ expected rows

    private static ImportRow Node(int index, string key, string? parentKey, string kind, string title, int position) =>
        new(index, ImportRowKind.Node, key, parentKey, null, kind, title, null, null, null, null, null, null, position);

    private static ImportRow Item(
        int index,
        string key,
        string parentKey,
        string title,
        string? description,
        string priority,
        string state,
        int position) =>
        new(index, ImportRowKind.Item, key, parentKey, null, null, title, description, null, null, priority, state, null, position);

    private static ImportRow Case(
        int index,
        string key,
        string itemKey,
        string code,
        string title,
        string? description,
        string? precondition,
        string steps,
        string priority,
        string state) =>
        new(index, ImportRowKind.Case, key, null, itemKey, null, title, description, precondition, steps, priority, state, code, null);

    // ------------------------------------------------------------------ AC14: the file's order and keys

    /// <summary>
    /// The documented emission order and the deterministic keys, pinned row by row: depth-first pre-order over the nodes,
    /// each node row followed by its items in <c>(position, id)</c>, each item by its cases in <c>(code, id)</c>.
    /// </summary>
    [Fact]
    public async Task Export_KeyNumberingAndOrder()
    {
        await using var world = await StartAsync();
        await SeedTreeAsync(world);

        var export = await ExportAsync(world);

        export.Result.IsSuccess.ShouldBeTrue(export.Result.Error?.Message);

        ImportRow[] expected =
        [
            Node(1, "n1", null, "module", "Shop", 0),
            Node(2, "n2", "n1", "feature", "Checkout", 0),
            Node(3, "n3", "n2", "category", "Payment", 0),
            Item(4, "i1", "n3", "Pay with a card", "Visa or Mastercard", "critical", "active", 0),
            Case(5, "c1", "i1", "SHOP-001", "Card accepted", "Happy path", null, CardOkSteps, "critical", "approved"),
            Case(6, "c2", "i1", "SHOP-002", "Card declined", null, "A card is declined", CardDeclinedSteps, "high", "approved"),
            Item(7, "i2", "n3", "Pay with cash", "Cash on delivery", "low", "draft", 1),
            Node(8, "n4", "n3", "sub_category", "Declined", 0),
            Item(9, "i3", "n4", "Retry after decline", null, "medium", "draft", 0),
            Case(10, "c3", "i3", "SHOP-003", "Retry after decline", null, null, RetrySteps, "medium", "draft"),
            Node(11, "n5", "n1", "feature", "Cart", 1),
            Node(12, "n6", null, "module", "Orders", 1),
        ];

        export.Result.Value.ShouldBe(expected);

        // The file carries no database identity: a key is the row's own n#/i#/c# and nothing else.
        export.Result.Value.Select(row => row.Key)
            .ShouldBe(["n1", "n2", "n3", "i1", "c1", "c2", "i2", "n4", "i3", "c3", "n5", "n6"]);
    }

    /// <summary>Two exports of an unchanged tree are the same rows, field by field — no id, timestamp or counter leaks in.</summary>
    [Fact]
    public async Task Export_Deterministic_TwiceIdentical()
    {
        await using var world = await StartAsync();
        await SeedTreeAsync(world);

        var first = await ExportAsync(world);
        var second = await ExportAsync(world);

        first.Result.IsSuccess.ShouldBeTrue(first.Result.Error?.Message);
        second.Result.IsSuccess.ShouldBeTrue(second.Result.Error?.Message);

        second.Result.Value.Count.ShouldBe(first.Result.Value.Count);

        foreach (var (left, right) in first.Result.Value.Zip(second.Result.Value))
        {
            right.ShouldBe(left);
        }
    }

    // ------------------------------------------------------------------ scoping

    /// <summary>
    /// <c>node_id</c> exports that node's subtree and nothing else; the subtree's root carries no <c>parent_key</c>,
    /// because its parent is not in the file, and the numbering starts at that root.
    /// </summary>
    [Fact]
    public async Task Export_NodeSubtree_ScopesToSubtree()
    {
        await using var world = await StartAsync();
        var tree = await SeedTreeAsync(world);

        var export = await ExportAsync(world, nodeId: tree.Checkout);

        export.Result.IsSuccess.ShouldBeTrue(export.Result.Error?.Message);

        ImportRow[] expected =
        [
            Node(1, "n1", null, "feature", "Checkout", 0),
            Node(2, "n2", "n1", "category", "Payment", 0),
            Item(3, "i1", "n2", "Pay with a card", "Visa or Mastercard", "critical", "active", 0),
            Case(4, "c1", "i1", "SHOP-001", "Card accepted", "Happy path", null, CardOkSteps, "critical", "approved"),
            Case(5, "c2", "i1", "SHOP-002", "Card declined", null, "A card is declined", CardDeclinedSteps, "high", "approved"),
            Item(6, "i2", "n2", "Pay with cash", "Cash on delivery", "low", "draft", 1),
            Node(7, "n3", "n2", "sub_category", "Declined", 0),
            Item(8, "i3", "n3", "Retry after decline", null, "medium", "draft", 0),
            Case(9, "c3", "i3", "SHOP-003", "Retry after decline", null, null, RetrySteps, "medium", "draft"),
        ];

        export.Result.Value.ShouldBe(expected);
    }

    /// <summary>
    /// <c>include</c> filters the kinds and never the numbering: a cases-only export carries the same <c>c#</c> keys and
    /// the same <c>item_key</c>s the full export gave those cases, and an items-only one the same <c>i#</c>/<c>parent_key</c>.
    /// </summary>
    [Fact]
    public async Task Export_Include_FiltersKindsKeepsKeys()
    {
        await using var world = await StartAsync();
        await SeedTreeAsync(world);

        var full = await ExportAsync(world);
        full.Result.IsSuccess.ShouldBeTrue(full.Result.Error?.Message);

        var casesOnly = await ExportAsync(world, include: ImportInclude.Cases);
        casesOnly.Result.IsSuccess.ShouldBeTrue(casesOnly.Result.Error?.Message);
        casesOnly.Result.Value.ShouldAllBe(row => row.Kind == ImportRowKind.Case);

        // Same keys, same item references; only the 1-based index is the file's own (the file is shorter).
        casesOnly.Result.Value.Select(row => (row.Key, row.ItemKey, row.Code))
            .ShouldBe(full.Result.Value
                .Where(row => row.Kind == ImportRowKind.Case)
                .Select(row => (row.Key, row.ItemKey, row.Code)));

        casesOnly.Result.Value.Select(row => row.Index).ShouldBe([1, 2, 3]);

        var itemsOnly = await ExportAsync(world, include: ImportInclude.Items);
        itemsOnly.Result.IsSuccess.ShouldBeTrue(itemsOnly.Result.Error?.Message);
        itemsOnly.Result.Value.ShouldAllBe(row => row.Kind == ImportRowKind.Item);
        itemsOnly.Result.Value.Select(row => (row.Key, row.ParentKey, row.Title))
            .ShouldBe(full.Result.Value
                .Where(row => row.Kind == ImportRowKind.Item)
                .Select(row => (row.Key, row.ParentKey, row.Title)));

        // The nodes an items-only export omits still name the parents of its rows: n3 and n4 are the full export's keys.
        itemsOnly.Result.Value.Select(row => row.ParentKey).ShouldBe(["n3", "n3", "n4"]);
    }

    /// <summary>
    /// Only live rows are exported: a tombstoned case, item or node disappears, and a tombstoned node takes its whole
    /// subtree with it (AC7/AC8 leave the rows in place, so an export that ignored <c>deleted_at</c> would resurrect them).
    /// </summary>
    [Fact]
    public async Task Export_ExcludesSoftDeleted()
    {
        await using var world = await StartAsync();
        var tree = await SeedTreeAsync(world);

        await ExecuteAsync(world, $"""
            update public.manual_test_case set deleted_at = now() where id = '{tree.CardDeclined:D}';
            update public.checklist_item set deleted_at = now() where id = '{tree.CashItem:D}';
            update public.feature_node set deleted_at = now() where id = '{tree.Declined:D}';
            """);

        var export = await ExportAsync(world);

        export.Result.IsSuccess.ShouldBeTrue(export.Result.Error?.Message);

        export.Result.Value.Select(row => (row.Kind, row.Key, row.ParentKey, row.Title)).ShouldBe(
        [
            (ImportRowKind.Node, "n1", (string?)null, "Shop"),
            (ImportRowKind.Node, "n2", "n1", "Checkout"),
            (ImportRowKind.Node, "n3", "n2", "Payment"),
            (ImportRowKind.Item, "i1", "n3", "Pay with a card"),
            (ImportRowKind.Case, "c1", null, "Card accepted"),
            (ImportRowKind.Node, "n4", "n1", "Cart"),
            (ImportRowKind.Node, "n5", null, "Orders"),
        ]);
    }

    // ------------------------------------------------------------------ P3: the statement budget

    /// <summary>
    /// A whole-project export of all three kinds is four statements — the project probe plus one per kind — whatever the
    /// number of rows: the same count answers a twelve-row tree and a five-hundred-row one.
    /// </summary>
    [Fact]
    public async Task Export_StatementCount_ConstantInRowCount()
    {
        await using var small = await StartAsync();
        await SeedTreeAsync(small);

        var smallExport = await ExportAsync(small);
        smallExport.Result.IsSuccess.ShouldBeTrue(smallExport.Result.Error?.Message);
        smallExport.Result.Value.Count.ShouldBe(12);
        smallExport.Statements.ShouldBe(4);

        await using var large = await StartAsync();
        var tree = NewTree();

        // Two dollars, so the holes are double braces and the steps document keeps its own single braces.
        await ExecuteAsync(large, $$"""
            insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
            values ('{{tree.Shop:D}}', '{{large.Org:D}}', '{{large.Project:D}}', null, 'module', 'Shop', 0);

            insert into public.checklist_item
              (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            values ('{{tree.CardItem:D}}', '{{large.Org:D}}', '{{large.Project:D}}', '{{tree.Shop:D}}', 'Pay', 'high', 'active', 'manual', 0);

            insert into public.manual_test_case
              (id, org_id, project_id, checklist_item_id, code, title, steps, priority, state, source)
            select gen_random_uuid(), '{{large.Org:D}}', '{{large.Project:D}}', '{{tree.CardItem:D}}',
                   'SHOP-' || lpad(generated.number::text, 3, '0'),
                   'Case ' || generated.number::text,
                   '{"steps":[{"no":1,"action":"Act","expected":"Result"}]}'::jsonb,
                   'medium', 'draft', 'manual'
              from generate_series(1, 499) as generated(number);
            """);

        var largeExport = await ExportAsync(large);
        largeExport.Result.IsSuccess.ShouldBeTrue(largeExport.Result.Error?.Message);
        largeExport.Result.Value.Count.ShouldBe(501);
        largeExport.Statements.ShouldBe(smallExport.Statements);
    }

    // ------------------------------------------------------------------ AC16: the row cap

    /// <summary>
    /// The export cap is the import cap: exactly 5 000 rows answer, 5 001 answer <c>413 payload_too_large</c> with
    /// <c>/node_id</c> as the pointer, and a scoped export of the same project is the documented way through.
    /// </summary>
    [Fact]
    public async Task Export_Over5000Rows_413()
    {
        await using var world = await StartAsync();

        await ExecuteAsync(world, $"""
            insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
            select gen_random_uuid(), '{world.Org:D}', '{world.Project:D}', null, 'module',
                   'Node ' || generated.number::text, generated.number
              from generate_series(0, 4999) as generated(number);
            """);

        var atCap = await ExportAsync(world);
        atCap.Result.IsSuccess.ShouldBeTrue(atCap.Result.Error?.Message);
        atCap.Result.Value.Count.ShouldBe(ImportLimits.MaxDataRows);

        await ExecuteAsync(world, $"""
            insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
            values (gen_random_uuid(), '{world.Org:D}', '{world.Project:D}', null, 'module', 'One more', 5000);
            """);

        var overCap = await ExportAsync(world);

        overCap.Result.IsSuccess.ShouldBeFalse();
        overCap.Result.Error!.Status.ShouldBe(413);
        overCap.Result.Error.Code.ShouldBe("payload_too_large");
        overCap.Result.Error.Details.ShouldNotBeNull();
        overCap.Result.Error.Details!.Count.ShouldBe(1);
        overCap.Result.Error.Details![0].Path.ShouldBe("/node_id");

        // The detail carries the shared size token (ContractValidationError.BodyTooLargeReason), never the error code again.
        overCap.Result.Error.Details![0].Reason.ShouldBe("body_too_large");

        // The refusal names the way through: one node's subtree is a bounded scope.
        var scoped = await ExportAsync(world, nodeId: await FirstRootAsync(world));
        scoped.Result.IsSuccess.ShouldBeTrue(scoped.Result.Error?.Message);
        scoped.Result.Value.Count.ShouldBe(1);
        scoped.Statements.ShouldBe(5);
    }

    /// <summary>The id of one root module of the project, read as the owner.</summary>
    private async Task<Guid> FirstRootAsync(World world)
    {
        await using var read = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));

        return await read.ScalarAsync<Guid>(
            "select id from public.feature_node where org_id = @org_id and project_id = @project_id and parent_id is null order by position limit 1",
            ("org_id", world.Org),
            ("project_id", world.Project));
    }

    // ------------------------------------------------------------------ AC10: the 404s

    /// <summary>
    /// An unknown node, a node of another project of the same tenant and another tenant's project are all
    /// <c>404 not_found</c> — the tree route's own answer, so an export can never be used to probe which ids exist (AC10).
    /// The same-tenant case is the one that proves the node probe carries <c>project_id</c>: a probe that only filtered
    /// <c>org_id</c> would answer it.
    /// </summary>
    [Fact]
    public async Task Export_OtherProjectNode_404()
    {
        await using var world = await StartAsync();
        await SeedTreeAsync(world);

        var siblingProject = await SeedProjectAsync(world.Database, world.Org, "billing");
        var foreignNode = Guid.CreateVersion7();

        await ExecuteAsync(world, $"""
            insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
            values ('{foreignNode:D}', '{world.Org:D}', '{siblingProject:D}', null, 'module', 'Billing', 0);
            """);

        // Another project of this tenant, addressed on this project's route: no such node here.
        var sameTenant = await ExportAsync(world, nodeId: foreignNode);
        sameTenant.Result.IsSuccess.ShouldBeFalse();
        sameTenant.Result.Error!.Status.ShouldBe(404);
        sameTenant.Result.Error.Code.ShouldBe("not_found");

        // Another tenant's project: the project probe answers the same 404.
        var (_, otherProject) = await SeedTenantAsync(world.Database, "globex", "billing");
        var otherTenant = await ExportAsync(world, projectId: otherProject);
        otherTenant.Result.IsSuccess.ShouldBeFalse();
        otherTenant.Result.Error!.Status.ShouldBe(404);
        otherTenant.Result.Error.Code.ShouldBe("not_found");

        // A node id that never existed.
        var unknown = await ExportAsync(world, nodeId: Guid.CreateVersion7());
        unknown.Result.IsSuccess.ShouldBeFalse();
        unknown.Result.Error!.Status.ShouldBe(404);
    }

    /// <summary>
    /// Row level security scopes the read: exporting one tenant's project never carries another tenant's rows, and the
    /// second tenant's own project exports only its own tree.
    /// </summary>
    [Fact]
    public async Task Export_RlsScoped()
    {
        await using var world = await StartAsync();
        var mine = await SeedTreeAsync(world);

        var (otherOrg, otherProject) = await SeedTenantAsync(world.Database, "globex", "billing");
        var theirs = Guid.CreateVersion7();

        await ExecuteAsync(world, $"""
            insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
            values ('{theirs:D}', '{otherOrg:D}', '{otherProject:D}', null, 'module', 'Globex hidden', 0);
            """);

        var mineExport = await ExportAsync(world);
        mineExport.Result.IsSuccess.ShouldBeTrue(mineExport.Result.Error?.Message);
        mineExport.Result.Value.ShouldAllBe(row => row.Title != "Globex hidden");
        mineExport.Result.Value.Count.ShouldBe(12);

        // The second tenant sees its own project and nothing of the first.
        var theirExport = await ExportAsync(world, orgId: otherOrg, projectId: otherProject);
        theirExport.Result.IsSuccess.ShouldBeTrue(theirExport.Result.Error?.Message);
        theirExport.Result.Value.Count.ShouldBe(1);
        theirExport.Result.Value[0].Title.ShouldBe("Globex hidden");

        // The root module of the first tenant is invisible under the second tenant's scope.
        var crossTenant = await ExportAsync(world, nodeId: mine.Shop, orgId: otherOrg, projectId: otherProject);
        crossTenant.Result.IsSuccess.ShouldBeFalse();
        crossTenant.Result.Error!.Status.ShouldBe(404);
    }

    /// <summary>
    /// A delegating <see cref="ITenantDb"/> that counts the commands the exporter issues: Dapper sends one command per
    /// call, so the count is the statement budget of P3 measured at the seam the module itself uses.
    /// </summary>
    /// <param name="inner">The scope-bound Dapper path.</param>
    private sealed class CountingTenantDb(ITenantDb inner) : ITenantDb
    {
        /// <summary>The number of commands issued so far.</summary>
        internal int Statements { get; private set; }

        /// <inheritdoc />
        public DbConnection Connection => inner.Connection;

        /// <inheritdoc />
        public DbTransaction Transaction => inner.Transaction;

        /// <inheritdoc />
        public Task<IReadOnlyList<T>> QueryAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            Statements++;

            return inner.QueryAsync<T>(sql, parameters, cancellationToken);
        }

        /// <inheritdoc />
        public Task<T?> QuerySingleOrDefaultAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            Statements++;

            return inner.QuerySingleOrDefaultAsync<T>(sql, parameters, cancellationToken);
        }

        /// <inheritdoc />
        public Task<int> ExecuteAsync(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            Statements++;

            return inner.ExecuteAsync(sql, parameters, cancellationToken);
        }

        /// <inheritdoc />
        public Task<T?> ScalarAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            Statements++;

            return inner.ScalarAsync<T>(sql, parameters, cancellationToken);
        }
    }
}
