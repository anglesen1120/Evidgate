using System.Data.Common;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Application.Import;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringExporterTests
{
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

    // ------------------------------------------------------------------ AC16: the caps on a filtered and a text-heavy file

    /// <summary>
    /// AC16's row cap is the cap on the rows <em>this answer</em> carries, not on the project: a project of 6 000 nodes,
    /// one item and 100 cases answers <c>include=cases</c> with its 100 case rows — and with the full tree's keys, because
    /// the walk that numbers them still saw the whole tree — while the whole-project export of the same tree is the 413.
    /// </summary>
    [Fact]
    public async Task Export_IncludeCases_OnAProjectOverTheRowCap_Succeeds()
    {
        await using var world = await StartAsync();
        var tree = NewTree();

        // The whole project is over the 5 000-row cap (6 000 nodes + 1 item + 100 cases), the cases are not. The item
        // hangs under the node created *last* (position 5999), so a supporting node read truncated at the row cap would
        // lose it — and with it every case — which is exactly what this case pins.
        await ExecuteAsync(world, $$"""
            insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
            select case when generated.number = 0 then '{{tree.Shop:D}}'::uuid else gen_random_uuid() end,
                   '{{world.Org:D}}', '{{world.Project:D}}', null, 'module',
                   'Node ' || generated.number::text, generated.number::int
              from generate_series(0, 5999) as generated(number);

            insert into public.checklist_item
              (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            select '{{tree.CardItem:D}}', '{{world.Org:D}}', '{{world.Project:D}}', last_node.id,
                   'Pay', 'high', 'active', 'manual', 0
              from (select id from public.feature_node
                     where project_id = '{{world.Project:D}}' order by position desc, id desc limit 1) as last_node;

            insert into public.manual_test_case
              (id, org_id, project_id, checklist_item_id, code, title, steps, priority, state, source)
            select gen_random_uuid(), '{{world.Org:D}}', '{{world.Project:D}}', '{{tree.CardItem:D}}',
                   'SHOP-' || lpad(generated.number::text, 3, '0'),
                   'Case ' || generated.number::text,
                   '{"steps":[{"no":1,"action":"Act","expected":"Result"}]}'::jsonb,
                   'medium', 'draft', 'manual'
              from generate_series(1, 100) as generated(number);
            """);

        var wholeProject = await ExportAsync(world);
        wholeProject.Result.IsSuccess.ShouldBeFalse("6 101 emitted rows are over the 5 000-row cap");
        wholeProject.Result.Error!.Status.ShouldBe(413);

        var cases = await ExportAsync(world, include: ImportInclude.Cases);

        cases.Result.IsSuccess.ShouldBeTrue(cases.Result.Error?.Message);
        cases.Result.Value.Count.ShouldBe(100, "only the rows of the kinds that were asked for are emitted");
        cases.Result.Value.ShouldAllBe(row => row.Kind == ImportRowKind.Case);

        // Keys are the full tree's keys even in a filtered file: the walk numbered every row of every kind.
        cases.Result.Value[0].Key.ShouldBe("c1");
        cases.Result.Value[^1].Key.ShouldBe("c100");
        cases.Result.Value.ShouldAllBe(row => row.ItemKey == "i1", "a case row still carries its item's key");
        cases.Statements.ShouldBe(4, "the probe plus one statement per kind: the statement count does not grow");
    }

    /// <summary>
    /// AC14's byte half of the same cap: 100 cases whose <c>steps</c> documents are 60 000 characters each make a file of
    /// six million characters — under the row cap and over the 5 MiB body cap of the import — so the export is the 413
    /// with the same <c>/node_id</c> pointer and <c>body_too_large</c> reason instead of a download no import accepts. A
    /// third of the same text still exports, so the bound is a cap and not an over-refusal.
    /// </summary>
    [Fact]
    public async Task Export_TextOverTheBodyCap_413()
    {
        await using var world = await StartAsync();
        var tree = NewTree();

        await ExecuteAsync(world, $$"""
            insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
            values ('{{tree.Shop:D}}', '{{world.Org:D}}', '{{world.Project:D}}', null, 'module', 'Shop', 0);

            insert into public.checklist_item
              (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            values ('{{tree.CardItem:D}}', '{{world.Org:D}}', '{{world.Project:D}}', '{{tree.Shop:D}}',
                    'Pay', 'high', 'active', 'manual', 0);

            insert into public.manual_test_case
              (id, org_id, project_id, checklist_item_id, code, title, steps, priority, state, source)
            select gen_random_uuid(), '{{world.Org:D}}', '{{world.Project:D}}', '{{tree.CardItem:D}}',
                   'SHOP-' || lpad(generated.number::text, 3, '0'),
                   'Case ' || generated.number::text,
                   jsonb_build_object('steps', jsonb_build_array(jsonb_build_object(
                       'no', 1,
                       'action', repeat('x', 60000),
                       'expected', 'Result'))),
                   'medium', 'draft', 'manual'
              from generate_series(1, 40) as generated(number);
            """);

        var third = await ExportAsync(world);
        third.Result.IsSuccess.ShouldBeTrue(third.Result.Error?.Message);
        third.Result.Value.Count.ShouldBe(42, "the control export is under the byte cap");

        await ExecuteAsync(world, $$"""
            insert into public.manual_test_case
              (id, org_id, project_id, checklist_item_id, code, title, steps, priority, state, source)
            select gen_random_uuid(), '{{world.Org:D}}', '{{world.Project:D}}', '{{tree.CardItem:D}}',
                   'SHOP-' || lpad(generated.number::text, 3, '0'),
                   'Case ' || generated.number::text,
                   jsonb_build_object('steps', jsonb_build_array(jsonb_build_object(
                       'no', 1,
                       'action', repeat('x', 60000),
                       'expected', 'Result'))),
                   'medium', 'draft', 'manual'
              from generate_series(41, 100) as generated(number);
            """);

        var overCap = await ExportAsync(world);

        overCap.Result.IsSuccess.ShouldBeFalse("100 × 60 000 characters is over the 5 MiB body cap");
        overCap.Result.Error!.Status.ShouldBe(413);
        overCap.Result.Error.Code.ShouldBe("payload_too_large");
        overCap.Result.Error.Details.ShouldNotBeNull();
        overCap.Result.Error.Details!.Count.ShouldBe(1);
        overCap.Result.Error.Details![0].Path.ShouldBe("/node_id");
        overCap.Result.Error.Details![0].Reason.ShouldBe("body_too_large");
        overCap.Result.Error.Message.ShouldNotContain("xxx", Shouldly.Case.Sensitive, "the refusal never echoes a cell");
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
        var (_, otherProject) = await SeedTenantAsync(world.Database, Unique.Slug("globex"), "billing");
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

        var (otherOrg, otherProject) = await SeedTenantAsync(world.Database, Unique.Slug("globex"), "billing");
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
