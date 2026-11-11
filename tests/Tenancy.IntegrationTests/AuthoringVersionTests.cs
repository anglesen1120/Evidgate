using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T1.4 against a real PostgreSQL: <c>AuthoringVersionWriter</c> is the one code path that mutates an entity and
/// appends an <c>authoring_version</c> row (AC5), its AC7 non-empty guard, AC22's move, AC8's restore, the row lock that
/// serialises two writers of one entity, and the §4 code allocator.
/// </summary>
/// <remarks>
/// Every call runs through the module seam (<c>AddAuthoringModule</c>) over a real <c>ITenantScopeFactory</c>, so the
/// statements run on the app role inside a tenant transaction and RLS scopes them — the production path, not a stub.
/// </remarks>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class AuthoringVersionTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuthoringVersionTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(DedicatedDatabase Database, Guid Org, Guid Project, NpgsqlDataSource DataSource, ServiceProvider Provider)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Provider.DisposeAsync();
                await DataSource.DisposeAsync();
            }
            finally
            {
                // The database is this world's to drop even when the module's own disposal throws.
                await Database.DisposeAsync();
            }
        }
    }

    private Task<World> StartAsync() =>
        _fixture.StartDedicatedAsync(async database =>
        {
            // Dedicated database: several assertions count whole tables, e.g. authoring_version.
            var (org, project) = await SeedAsync(database, "acme", "checkout");
            var (dataSource, provider) = Module(database);
            return new World(database, org, project, dataSource, provider);
        });

    /// <summary>One module container over the app-role connection string (the seam, not a hand-built writer).</summary>
    private (NpgsqlDataSource DataSource, ServiceProvider Provider) Module(string database)
    {
        var dataSource = NpgsqlDataSource.Create(_fixture.AppConnectionString(database));
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddEvidgateTenancy();

        // The module's lazy ContractSchemaSet resolves IHostEnvironment, which a test container has none of. The explicit
        // contracts directory overload registers the compiled set first, so the module's TryAdd leaves it alone: the
        // steps schema is the real contracts/schemas/authoring-steps.v1.json, not a stand-in.
        services.AddEvidgateContractValidation(Path.Combine(RlsExceptionFile.RepositoryRoot, "contracts"));
        services.AddAuthoringModule();

        return (dataSource, services.BuildServiceProvider());
    }

    private async Task<(Guid Org, Guid Project)> SeedAsync(string database, string orgSlug, string projectSlug)
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

    // ------------------------------------------------------------------ AC5: one row per changed entity

    [Fact]
    public async Task Append_WritesOneRowPerChangedEntity_OneBatchId()
    {
        await using var world = await StartAsync();
        var module = Guid.CreateVersion7();
        var feature = Guid.CreateVersion7();
        var category = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();
        var batch = Guid.CreateVersion7();

        var result = await AppendAsync(
            world,
            batch,
            [
                EntityChange.Create(AuthoringEntityKind.FeatureNode, module, NodeContent(FeatureNodeKinds.Module, "Shop", 0, null)),
                EntityChange.Create(AuthoringEntityKind.FeatureNode, feature, NodeContent(FeatureNodeKinds.Feature, "Checkout", 0, module)),
                EntityChange.Create(AuthoringEntityKind.FeatureNode, category, NodeContent(FeatureNodeKinds.Category, "Payment", 0, feature)),
                EntityChange.Create(AuthoringEntityKind.ChecklistItem, item, ItemContent(category, "Pay with a card", 0)),
            ]);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value!.Versions.Count.ShouldBe(4);
        result.Value.UnchangedEntityIds.ShouldBeEmpty();
        result.Value.Versions.ShouldAllBe(version => version.BatchId == batch && version.VersionNo == 1);
        result.Value.Versions.ShouldAllBe(version => version.ContentHash.StartsWith("sha256:", StringComparison.Ordinal));

        // One row per changed entity, one batch, version_no 1, source manual, and the snapshot — never the row's fields.
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.authoring_version")).ShouldBe(4);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(distinct batch_id) from public.authoring_version where batch_id = '{batch:D}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            "select string_agg(distinct change_kind || '|' || source || '|' || version_no::text, ',' order by change_kind || '|' || source || '|' || version_no::text) from public.authoring_version"))
            .ShouldBe("create|manual|1");

        // AC5's snapshot: the exact member set of §4, no server field.
        (await _fixture.ScalarAsync<string>(
            world.Database,
            "select content->>'kind' || '|' || (content->>'parent_id') || '|' || (content->>'position') || '|' || (content->>'title') "
            + "|| '|' || (content ? 'id')::text || '|' || (content ? 'org_id')::text from public.authoring_version "
            + $"where entity_kind = 'feature_node' and entity_id = '{feature:D}'"))
            .ShouldBe("feature|" + module.ToString("D") + "|0|Checkout|false|false");

        // AC5's no-op: an identical snapshot writes no version and leaves the row alone.
        var before = await _fixture.ScalarAsync<int>(
            world.Database,
            $"select version from public.feature_node where id = '{feature:D}'");
        var noOp = await AppendAsync(
            world,
            Guid.CreateVersion7(),
            [EntityChange.Update(AuthoringEntityKind.FeatureNode, feature, NodeContent(FeatureNodeKinds.Feature, "Checkout", 0, module))]);

        noOp.IsSuccess.ShouldBeTrue(noOp.Error?.Message);
        noOp.Value!.Versions.ShouldBeEmpty();
        noOp.Value.UnchangedEntityIds.ShouldBe([feature]);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.authoring_version where entity_id = '{feature:D}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select version from public.feature_node where id = '{feature:D}'")).ShouldBe(before);
    }

    // ------------------------------------------------------------------ AC5: the writer's own refusals

    [Fact]
    public async Task Append_RefusesEmptyBatchAndDuplicateVersionNo()
    {
        await using var world = await StartAsync();
        var module = Guid.CreateVersion7();

        // An empty batch id is a programming error the writer answers itself, before any statement runs.
        var empty = await AppendAsync(
            world,
            Guid.Empty,
            [EntityChange.Create(AuthoringEntityKind.FeatureNode, module, NodeContent(FeatureNodeKinds.Module, "Shop", 0, null))]);

        empty.IsSuccess.ShouldBeFalse();
        empty.Error!.Code.ShouldBe(Evidgate.BuildingBlocks.SharedKernel.Api.ApiErrorCode.RequestInvalid);
        empty.Error.Details!.ShouldContain(detail => detail.Path == "/batch_id" && detail.Reason == "required");

        // One entity twice in one call would write two rows of one version: refused as duplicate_entity.
        var duplicate = await AppendAsync(
            world,
            Guid.CreateVersion7(),
            [
                EntityChange.Create(AuthoringEntityKind.FeatureNode, module, NodeContent(FeatureNodeKinds.Module, "Shop", 0, null)),
                EntityChange.Update(AuthoringEntityKind.FeatureNode, module, NodeContent(FeatureNodeKinds.Module, "Shop 2", 0, null)),
            ]);

        duplicate.IsSuccess.ShouldBeFalse();
        duplicate.Error!.Details!.ShouldContain(detail => detail.Reason == "duplicate_entity");
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.authoring_version")).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.feature_node")).ShouldBe(0);

        // A create of an id that already exists is a caller error, not a 23505.
        var created = await AppendAsync(
            world,
            Guid.CreateVersion7(),
            [EntityChange.Create(AuthoringEntityKind.FeatureNode, module, NodeContent(FeatureNodeKinds.Module, "Shop", 0, null))]);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var again = await AppendAsync(
            world,
            Guid.CreateVersion7(),
            [EntityChange.Create(AuthoringEntityKind.FeatureNode, module, NodeContent(FeatureNodeKinds.Module, "Shop", 0, null))]);
        again.IsSuccess.ShouldBeFalse();
        again.Error!.Details!.ShouldContain(detail => detail.Reason == "entity_exists");

        // AC2: a live sibling already holds position 0.
        var taken = await AppendAsync(
            world,
            Guid.CreateVersion7(),
            [EntityChange.Create(AuthoringEntityKind.FeatureNode, Guid.CreateVersion7(), NodeContent(FeatureNodeKinds.Module, "Other", 0, null))]);
        taken.IsSuccess.ShouldBeFalse();
        taken.Error!.Details!.ShouldContain(detail => detail.Path == "/changes/0/content/position" && detail.Reason == "position_taken");

        // AC1: a feature cannot stand at the root.
        var rootless = await AppendAsync(
            world,
            Guid.CreateVersion7(),
            [EntityChange.Create(AuthoringEntityKind.FeatureNode, Guid.CreateVersion7(), NodeContent(FeatureNodeKinds.Feature, "Root", 0, null))]);
        rootless.IsSuccess.ShouldBeFalse();
        rootless.Error!.Code.ShouldBe(Evidgate.BuildingBlocks.SharedKernel.Api.ApiErrorCode.AuthoringParentInvalid);
        rootless.Error.Details!.ShouldContain(detail => detail.Reason == PlacementReason.ParentKindInvalid);
    }

    // ------------------------------------------------------------------ AC7: the non-empty guard

    [Fact]
    public async Task SoftDeleteChange_RunsNonEmptyGuard()
    {
        await using var world = await StartAsync();
        var module = Guid.CreateVersion7();
        var feature = Guid.CreateVersion7();
        var category = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();
        var manualCase = Guid.CreateVersion7();

        var built = await AppendAsync(
            world,
            Guid.CreateVersion7(),
            [
                EntityChange.Create(AuthoringEntityKind.FeatureNode, module, NodeContent(FeatureNodeKinds.Module, "Shop", 0, null)),
                EntityChange.Create(AuthoringEntityKind.FeatureNode, feature, NodeContent(FeatureNodeKinds.Feature, "Checkout", 0, module)),
                EntityChange.Create(AuthoringEntityKind.FeatureNode, category, NodeContent(FeatureNodeKinds.Category, "Payment", 0, feature)),
                EntityChange.Create(AuthoringEntityKind.ChecklistItem, item, ItemContent(category, "Pay with a card", 0)),
                EntityChange.Create(AuthoringEntityKind.ManualTestCase, manualCase, CaseContent(item, "SHOP-001", "Pay")),
            ]);
        built.IsSuccess.ShouldBeTrue(built.Error?.Message);

        // A node with a live child, a node with a live item and an item with a live case are all refused, nothing changes.
        var nodeWithChildren = await AppendAsync(
            world, Guid.CreateVersion7(), [EntityChange.SoftDelete(AuthoringEntityKind.FeatureNode, module, "cleanup")]);
        nodeWithChildren.IsSuccess.ShouldBeFalse();
        nodeWithChildren.Error!.Code.ShouldBe(Evidgate.BuildingBlocks.SharedKernel.Api.ApiErrorCode.AuthoringNotEmpty);
        nodeWithChildren.Error.Details!.ShouldContain(detail => detail.Reason == "node_has_children");

        var nodeWithItems = await AppendAsync(
            world, Guid.CreateVersion7(), [EntityChange.SoftDelete(AuthoringEntityKind.FeatureNode, category, "cleanup")]);
        nodeWithItems.IsSuccess.ShouldBeFalse();
        nodeWithItems.Error!.Details!.ShouldContain(detail => detail.Reason == "node_has_items");

        var itemWithCases = await AppendAsync(
            world, Guid.CreateVersion7(), [EntityChange.SoftDelete(AuthoringEntityKind.ChecklistItem, item, "cleanup")]);
        itemWithCases.IsSuccess.ShouldBeFalse();
        itemWithCases.Error!.Details!.ShouldContain(detail => detail.Reason == "item_has_cases");

        (await _fixture.ScalarAsync<long>(
            world.Database,
            "select count(*) from public.authoring_version where change_kind = 'delete'")).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            "select count(*) from public.feature_node where deleted_at is not null")).ShouldBe(0);

        // A leaf is a soft delete: the row stays, deleted_at set, version bumped, one delete row with its snapshot.
        var leaf = await AppendAsync(
            world, Guid.CreateVersion7(), [EntityChange.SoftDelete(AuthoringEntityKind.ManualTestCase, manualCase, "superseded")]);

        leaf.IsSuccess.ShouldBeTrue(leaf.Error?.Message);
        leaf.Value!.Versions.Single().VersionNo.ShouldBe(2);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.manual_test_case where id = '{manualCase:D}' and deleted_at is not null and version = 2"))
            .ShouldBe(1);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select change_kind || '|' || reason from public.authoring_version where entity_id = '{manualCase:D}' and version_no = 2"))
            .ShouldBe("delete|superseded");
    }

    // ------------------------------------------------------------------ AC22: the move

    [Fact]
    public async Task MoveManualCase_AppendsOneUpdateRow()
    {
        await using var world = await StartAsync();
        var module = Guid.CreateVersion7();
        var feature = Guid.CreateVersion7();
        var category = Guid.CreateVersion7();
        var from = Guid.CreateVersion7();
        var to = Guid.CreateVersion7();
        var manualCase = Guid.CreateVersion7();

        var built = await AppendAsync(
            world,
            Guid.CreateVersion7(),
            [
                EntityChange.Create(AuthoringEntityKind.FeatureNode, module, NodeContent(FeatureNodeKinds.Module, "Shop", 0, null)),
                EntityChange.Create(AuthoringEntityKind.FeatureNode, feature, NodeContent(FeatureNodeKinds.Feature, "Checkout", 0, module)),
                EntityChange.Create(AuthoringEntityKind.FeatureNode, category, NodeContent(FeatureNodeKinds.Category, "Payment", 0, feature)),
                EntityChange.Create(AuthoringEntityKind.ChecklistItem, from, ItemContent(category, "Pay with a card", 0)),
                EntityChange.Create(AuthoringEntityKind.ChecklistItem, to, ItemContent(category, "Pay with a wallet", 1)),
                EntityChange.Create(AuthoringEntityKind.ManualTestCase, manualCase, CaseContent(from, "SHOP-001", "Pay")),
            ]);
        built.IsSuccess.ShouldBeTrue(built.Error?.Message);

        var moved = await MoveAsync(world, manualCase, to, "split");

        moved.IsSuccess.ShouldBeTrue(moved.Error?.Message);
        moved.Value!.Moved.ShouldBeTrue();
        moved.Value.EntityVersion.ShouldBe(2);
        moved.Value.Version!.VersionNo.ShouldBe(2);

        // The code never changes, the new checklist_item_id is in content, and it is one update row.
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select checklist_item_id::text || '|' || code || '|' || version from public.manual_test_case where id = '{manualCase:D}'"))
            .ShouldBe(to.ToString("D") + "|SHOP-001|2");
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select change_kind || '|' || (content->>'checklist_item_id') || '|' || reason from public.authoring_version where entity_id = '{manualCase:D}' and version_no = 2"))
            .ShouldBe("update|" + to.ToString("D") + "|split");

        // A move onto the item it already sits under is AC5's no-op.
        var again = await MoveAsync(world, manualCase, to, null);
        again.IsSuccess.ShouldBeTrue(again.Error?.Message);
        again.Value!.Moved.ShouldBeFalse();
        again.Value.EntityVersion.ShouldBe(2);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.authoring_version where entity_id = '{manualCase:D}'")).ShouldBe(2);

        // A retired target is 409 invalid_transition, and so is a retired case.
        var retired = await AppendAsync(
            world,
            Guid.CreateVersion7(),
            [EntityChange.Update(AuthoringEntityKind.ChecklistItem, to, ItemContent(category, "Pay with a wallet", 1, ChecklistItemStateMachine.Retired)) with { Reason = "cleanup" }]);
        retired.IsSuccess.ShouldBeTrue(retired.Error?.Message);

        var refused = await MoveAsync(world, manualCase, to, null);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Code.ShouldBe(Evidgate.BuildingBlocks.SharedKernel.Api.ApiErrorCode.InvalidTransition);

        // A missing target is AC1's parent_not_found, indistinguishable from another project's item (AC10).
        var missing = await MoveAsync(world, manualCase, Guid.CreateVersion7(), null);
        missing.IsSuccess.ShouldBeFalse();
        missing.Error!.Code.ShouldBe(Evidgate.BuildingBlocks.SharedKernel.Api.ApiErrorCode.AuthoringParentInvalid);
        missing.Error.Details!.ShouldContain(detail => detail.Reason == PlacementReason.ParentNotFound);
    }

    // ------------------------------------------------------------------ AC8: restore

    [Fact]
    public async Task Restore_ReappendsSnapshot_AtMaxPlusOne()
    {
        await using var world = await StartAsync();
        var module = Guid.CreateVersion7();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var third = Guid.CreateVersion7();

        var built = await AppendAsync(
            world,
            Guid.CreateVersion7(),
            [
                EntityChange.Create(AuthoringEntityKind.FeatureNode, module, NodeContent(FeatureNodeKinds.Module, "Shop", 0, null)),
                EntityChange.Create(AuthoringEntityKind.FeatureNode, first, NodeContent(FeatureNodeKinds.Feature, "First", 0, module)),
                EntityChange.Create(AuthoringEntityKind.FeatureNode, second, NodeContent(FeatureNodeKinds.Feature, "Second", 1, module)),
            ]);
        built.IsSuccess.ShouldBeTrue(built.Error?.Message);

        var deleted = await AppendAsync(
            world, Guid.CreateVersion7(), [EntityChange.SoftDelete(AuthoringEntityKind.FeatureNode, first, "cleanup")]);
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        // Position 0 is free for a tombstone's old number, so a new node takes it and the restore must re-append at max + 1.
        var replacement = await AppendAsync(
            world,
            Guid.CreateVersion7(),
            [EntityChange.Create(AuthoringEntityKind.FeatureNode, third, NodeContent(FeatureNodeKinds.Feature, "Replacement", 0, module))]);
        replacement.IsSuccess.ShouldBeTrue(replacement.Error?.Message);

        var restored = await RestoreAsync(world, AuthoringEntityKind.FeatureNode, first);

        restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);
        restored.Value!.VersionNo.ShouldBe(3);

        // deleted_at cleared, the snapshot written back at max + 1 = 2, and the version row records that position.
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select (deleted_at is null)::text || '|' || position::text || '|' || version::text from public.feature_node where id = '{first:D}'"))
            .ShouldBe("true|2|3");
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select change_kind || '|' || (content->>'position') from public.authoring_version where entity_id = '{first:D}' and version_no = 3"))
            .ShouldBe("restore|2");

        // A restore of an entity row that is gone is a 404, never a resurrection.
        var gone = await RestoreAsync(world, AuthoringEntityKind.FeatureNode, Guid.CreateVersion7());
        gone.IsSuccess.ShouldBeFalse();
        gone.Error!.Code.ShouldBe(Evidgate.BuildingBlocks.SharedKernel.Api.ApiErrorCode.NotFound);

        // AC8's parent rule: a case never comes back under a soft-deleted item.
        var category = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();
        var manualCase = Guid.CreateVersion7();
        var tree = await AppendAsync(
            world,
            Guid.CreateVersion7(),
            [
                EntityChange.Create(AuthoringEntityKind.FeatureNode, category, NodeContent(FeatureNodeKinds.Category, "Payment", 0, second)),
                EntityChange.Create(AuthoringEntityKind.ChecklistItem, item, ItemContent(category, "Pay", 0)),
                EntityChange.Create(AuthoringEntityKind.ManualTestCase, manualCase, CaseContent(item, "SHOP-002", "Pay")),
            ]);
        tree.IsSuccess.ShouldBeTrue(tree.Error?.Message);

        (await AppendAsync(world, Guid.CreateVersion7(), [EntityChange.SoftDelete(AuthoringEntityKind.ManualTestCase, manualCase, "cleanup")]))
            .IsSuccess.ShouldBeTrue();
        (await AppendAsync(world, Guid.CreateVersion7(), [EntityChange.SoftDelete(AuthoringEntityKind.ChecklistItem, item, "cleanup")]))
            .IsSuccess.ShouldBeTrue();

        var orphan = await RestoreAsync(world, AuthoringEntityKind.ManualTestCase, manualCase);
        orphan.IsSuccess.ShouldBeFalse();
        orphan.Error!.Code.ShouldBe(Evidgate.BuildingBlocks.SharedKernel.Api.ApiErrorCode.AuthoringParentInvalid);
        orphan.Error.Details!.ShouldContain(detail => detail.Reason == PlacementReason.ParentDeleted);

        // §6: a restored item is active and a restored case is a draft again, whatever it was before.
        var itemRestored = await RestoreAsync(world, AuthoringEntityKind.ChecklistItem, item);
        itemRestored.IsSuccess.ShouldBeTrue(itemRestored.Error?.Message);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.checklist_item where id = '{item:D}'")).ShouldBe(ChecklistItemStateMachine.Active);

        var caseRestored = await RestoreAsync(world, AuthoringEntityKind.ManualTestCase, manualCase);
        caseRestored.IsSuccess.ShouldBeTrue(caseRestored.Error?.Message);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.manual_test_case where id = '{manualCase:D}'")).ShouldBe(ManualTestCaseStateMachine.Draft);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select change_kind from public.authoring_version where entity_id = '{manualCase:D}' order by version_no desc limit 1"))
            .ShouldBe(AuthoringWireValues.RestoreChangeKind);
    }

    // ------------------------------------------------------------------ the row lock

    [Fact]
    public async Task ConcurrentAppends_SerialiseOnTheEntityRow()
    {
        await using var world = await StartAsync();
        var entity = Guid.CreateVersion7();

        (await AppendAsync(
            world,
            Guid.CreateVersion7(),
            [EntityChange.Create(AuthoringEntityKind.FeatureNode, entity, NodeContent(FeatureNodeKinds.Module, "Shop", 0, null))]))
            .IsSuccess.ShouldBeTrue();

        var (dataSourceA, providerA) = Module(world.Database);
        var (dataSourceB, providerB) = Module(world.Database);
        await using var a = dataSourceA;
        await using var b = dataSourceB;
        await using var providerScopeA = providerA;
        await using var providerScopeB = providerB;

        using var written = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);

        var first = Task.Run(async () =>
        {
            using var services = providerA.CreateScope();
            var factory = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
            await using var scope = await factory.BeginAsync(world.Org, world.Project, false, TestContext.Current.CancellationToken);
            var writer = services.ServiceProvider.GetRequiredService<AuthoringVersionWriter>();
            var result = await writer.AppendAsync(
                world.Org,
                world.Project,
                Guid.CreateVersion7(),
                null,
                [EntityChange.Update(AuthoringEntityKind.FeatureNode, entity, NodeContent(FeatureNodeKinds.Module, "Shop A", 0, null))],
                null,
                TestContext.Current.CancellationToken);

            written.Release();
            await release.WaitAsync(TestContext.Current.CancellationToken);
            if (result.IsSuccess)
            {
                await scope.CommitAsync(TestContext.Current.CancellationToken);
            }

            return result;
        });

        // The first writer holds the entity row lock until it commits; the second must wait, then read the fresh version.
        (await written.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken)).ShouldBeTrue();

        var second = Task.Run(async () =>
        {
            using var services = providerB.CreateScope();
            var factory = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
            await using var scope = await factory.BeginAsync(world.Org, world.Project, false, TestContext.Current.CancellationToken);
            var writer = services.ServiceProvider.GetRequiredService<AuthoringVersionWriter>();
            var result = await writer.AppendAsync(
                world.Org,
                world.Project,
                Guid.CreateVersion7(),
                null,
                [EntityChange.Update(AuthoringEntityKind.FeatureNode, entity, NodeContent(FeatureNodeKinds.Module, "Shop B", 0, null))],
                null,
                TestContext.Current.CancellationToken);

            if (result.IsSuccess)
            {
                await scope.CommitAsync(TestContext.Current.CancellationToken);
            }

            return result;
        });

        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        release.Release();

        var firstResult = await first;
        var secondResult = await second;

        firstResult.IsSuccess.ShouldBeTrue(firstResult.Error?.Message);
        secondResult.IsSuccess.ShouldBeTrue(secondResult.Error?.Message);

        // Serialised, not raced: 1 create + two updates, versions 2 and 3, and never a 23505 on the entity key.
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select string_agg(version_no::text, ',' order by version_no) from public.authoring_version where entity_id = '{entity:D}'"))
            .ShouldBe("1,2,3");
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select version from public.feature_node where id = '{entity:D}'")).ShouldBe(3);
    }

    // ------------------------------------------------------------------ §4 / AC4: the allocator

    [Fact]
    public async Task Allocator_CommitsOnItsOwnConnection_SoARolledBackCallerBurnsTheNumber()
    {
        await using var world = await StartAsync();

        var first = await AllocateAsync(world, commit: true);
        var second = await AllocateAsync(world, commit: true);
        var burned = await AllocateAsync(world, commit: false);
        var fourth = await AllocateAsync(world, commit: true);

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        second.IsSuccess.ShouldBeTrue(second.Error?.Message);
        burned.IsSuccess.ShouldBeTrue(burned.Error?.Message);
        fourth.IsSuccess.ShouldBeTrue(fourth.Error?.Message);

        first.Value!.Number.ShouldBe(1);
        second.Value!.Number.ShouldBe(2);
        burned.Value!.Number.ShouldBe(3);
        fourth.Value!.Number.ShouldBe(4, "a rolled-back allocation is burned, never handed out twice");

        // The prefix is the migration's own derivation, not the allocator's: the seed inserted the project with only a
        // slug ("checkout"), so the 0039 BEFORE INSERT trigger filled case_prefix from evidgate.case_prefix_stem —
        // first four alphanumerics, upper-cased → CHEC. Hard-coded, so the derivation itself is under test.
        first.Value.Value.ShouldBe("CHEC-001");
        fourth.Value.Value.ShouldBe("CHEC-004");
        (await _fixture.ScalarAsync<string>(
            world.Database,
            "select case_prefix from public.project where id = '" + world.Project.ToString("D") + "'"))
            .ShouldBe("CHEC", "the trigger derived the prefix from the slug");
        (await _fixture.ScalarAsync<long>(
            world.Database,
            "select next_value from public.authoring_code_counter")).ShouldBe(5);

        // Two concurrent allocations in two flows never share a number.
        var (dataSourceA, providerA) = Module(world.Database);
        var (dataSourceB, providerB) = Module(world.Database);
        await using var a = dataSourceA;
        await using var b = dataSourceB;
        await using var providerScopeA = providerA;
        await using var providerScopeB = providerB;

        var left = Task.Run(() => AllocateWithAsync(providerA, world, commit: true));
        var right = Task.Run(() => AllocateWithAsync(providerB, world, commit: true));
        var results = await Task.WhenAll(left, right);

        results.ShouldAllBe(result => result.IsSuccess);
        results.Select(result => result.Value!.Number).Distinct().Count().ShouldBe(2);
        results.Select(result => result.Value!.Number).ShouldBe([5L, 6L], ignoreOrder: true);
    }

    // ------------------------------------------------------------------ AC5: the version history guard

    [Fact]
    public async Task Append_RefusesAVersionNoThatAlreadyExists_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var entity = Guid.CreateVersion7();
        var id = entity.ToString("D");

        (await AppendAsync(
            world,
            Guid.CreateVersion7(),
            [EntityChange.Create(AuthoringEntityKind.FeatureNode, entity, NodeContent(FeatureNodeKinds.Module, "Shop", 0, null))]))
            .IsSuccess.ShouldBeTrue();

        // The owner seeds a version row at 2 while the entity row still stands at 1, so the next update would reuse 2.
        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            await seed.ExecuteAsync(
                """
                insert into public.authoring_version
                       (id, org_id, project_id, entity_kind, entity_id, version_no, batch_id, change_kind, content,
                        content_hash, source)
                values (@id, @org_id, @project_id, 'feature_node', @entity_id, 2, @batch_id, 'update', @content::jsonb,
                        @content_hash, 'manual')
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", world.Org),
                ("project_id", world.Project),
                ("entity_id", entity),
                ("batch_id", Guid.CreateVersion7()),
                ("content", "{\"kind\":\"module\",\"title\":\"Shop\",\"position\":0,\"parent_id\":null}"),
                ("content_hash", "sha256:" + new string('a', 64)));
            await seed.CommitAsync();
        }

        var duplicate = await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Update(AuthoringEntityKind.FeatureNode, entity, NodeContent(FeatureNodeKinds.Module, "Shop renamed", 0, null)),
        ]);

        // The guard answers after the entity UPDATE has already run inside the caller's transaction: the new title and
        // the bumped row version are written on this connection when it refuses. Nothing persists only because the test
        // never commits the scope (disposal rolls it back); in production the out-of-step result is what makes the
        // caller roll back. The ON CONFLICT DO NOTHING row-count check is the second line of defence for the same shape
        // (a stored history that is not the row's own).
        duplicate.IsSuccess.ShouldBeFalse();
        duplicate.Error!.Code.ShouldBe(Evidgate.BuildingBlocks.SharedKernel.Api.ApiErrorCode.InvalidTransition);
        duplicate.Error.Message.ShouldContain("out of step");

        // Nothing was written: the row keeps its title and version, and the only version rows are the create's and the seed.
        (await _fixture.ScalarAsync<string>(
            world.Database,
            "select title from public.feature_node where id = '" + id + "'")).ShouldBe("Shop");
        (await _fixture.ScalarAsync<int>(
            world.Database,
            "select version from public.feature_node where id = '" + id + "'")).ShouldBe(1);
        (await _fixture.ScalarAsync<int>(
            world.Database,
            "select count(*)::int from public.authoring_version where entity_id = '" + id + "'")).ShouldBe(2);
    }

    // ------------------------------------------------------------------ §6: the restore state rule

    [Fact]
    public async Task Restore_ItemRecordsActiveState_AndAcceptsTheNextEdit()
    {
        await using var world = await StartAsync();
        var module = Guid.CreateVersion7();
        var feature = Guid.CreateVersion7();
        var category = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();

        (await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Create(AuthoringEntityKind.FeatureNode, module, NodeContent(FeatureNodeKinds.Module, "Shop", 0, null)),
            EntityChange.Create(AuthoringEntityKind.FeatureNode, feature, NodeContent(FeatureNodeKinds.Feature, "Checkout", 0, module)),
            EntityChange.Create(AuthoringEntityKind.FeatureNode, category, NodeContent(FeatureNodeKinds.Category, "Payment", 0, feature)),
            EntityChange.Create(AuthoringEntityKind.ChecklistItem, item, ItemContent(category, "Pay", 0)),
        ])).IsSuccess.ShouldBeTrue();

        // Retired, then soft-deleted: the state a restore must not bring back.
        (await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Update(AuthoringEntityKind.ChecklistItem, item, ItemContent(category, "Pay", 0, ChecklistItemStateMachine.Retired)) with { Reason = "cleanup" },
        ])).IsSuccess.ShouldBeTrue();
        (await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.SoftDelete(AuthoringEntityKind.ChecklistItem, item, "cleanup"),
        ])).IsSuccess.ShouldBeTrue();

        var restored = await RestoreAsync(world, AuthoringEntityKind.ChecklistItem, item);
        restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);

        var id = item.ToString("D");
        (await _fixture.ScalarAsync<string>(
            world.Database,
            "select state from public.checklist_item where id = '" + id + "'"))
            .ShouldBe(ChecklistItemStateMachine.Active, "§6: a restored item is active, not the retired state it was deleted in");
        (await _fixture.ScalarAsync<string>(
            world.Database,
            "select content->>'state' from public.authoring_version where entity_id = '" + id + "' order by version_no desc limit 1"))
            .ShouldBe(ChecklistItemStateMachine.Active, "the version snapshot records the state the row gets");

        // The row really is active: an ordinary edit is accepted, where a retired item refuses every edit.
        var edited = await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Update(AuthoringEntityKind.ChecklistItem, item, ItemContent(category, "Pay with a card", 0)),
        ]);
        edited.IsSuccess.ShouldBeTrue(edited.Error?.Message);
        (await _fixture.ScalarAsync<int>(
            world.Database,
            "select version from public.checklist_item where id = '" + id + "'"))
            .ShouldBeGreaterThan(4);
    }

    // ------------------------------------------------------------------ AC13/AC14: the steps document

    [Fact]
    public async Task Steps_AreValidatedAgainstAuthoringStepsV1_ForEveryCaller()
    {
        await using var world = await StartAsync();
        var (_, _, _, item) = await SeedLadderAsync(world);

        // Zero steps and 101 steps are the schema's own bounds, reported under /content/steps.
        var empty = await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Create(AuthoringEntityKind.ManualTestCase, Guid.CreateVersion7(), CaseWithSteps(item, "SHOP-101", "Empty", "{\"steps\":[]}")),
        ]);
        empty.IsSuccess.ShouldBeFalse();
        empty.Error!.Code.ShouldBe(Evidgate.BuildingBlocks.SharedKernel.Api.ApiErrorCode.RequestInvalid);
        empty.Error.Details!.ShouldContain(detail => detail.Path == "/changes/0/content/steps");

        var tooMany = string.Join(",", Enumerable.Range(1, 101).Select(no =>
            "{\"no\":" + no.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"action\":\"a\",\"expected\":\"b\"}"));
        var many = await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Create(AuthoringEntityKind.ManualTestCase, Guid.CreateVersion7(), CaseWithSteps(item, "SHOP-102", "Many", "{\"steps\":[" + tooMany + "]}")),
        ]);
        many.IsSuccess.ShouldBeFalse();
        many.Error!.Code.ShouldBe(Evidgate.BuildingBlocks.SharedKernel.Api.ApiErrorCode.RequestInvalid);
        many.Error.Details!.ShouldContain(detail => detail.Path == "/changes/0/content/steps");

        // An unknown member inside a step is refused too (additionalProperties: false).
        var unknown = await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Create(AuthoringEntityKind.ManualTestCase, Guid.CreateVersion7(), CaseWithSteps(item, "SHOP-103", "Unknown", "{\"steps\":[{\"no\":1,\"action\":\"a\",\"expected\":\"b\",\"bogus\":1}]}")),
        ]);
        unknown.IsSuccess.ShouldBeFalse();
        unknown.Error!.Code.ShouldBe(Evidgate.BuildingBlocks.SharedKernel.Api.ApiErrorCode.RequestInvalid);
        unknown.Error.Details!.ShouldContain(detail => detail.Path.StartsWith("/changes/0/content/steps/0", StringComparison.Ordinal));

        // A valid document passes and is stored.
        var valid = await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Create(AuthoringEntityKind.ManualTestCase, Guid.CreateVersion7(), CaseContent(item, "SHOP-104", "Valid")),
        ]);
        valid.IsSuccess.ShouldBeTrue(valid.Error?.Message);

        // The update path validates as well: a valid case may not be edited into an invalid document.
        var manualCase = valid.Value!.Versions.Single().EntityId;
        var broken = await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Update(AuthoringEntityKind.ManualTestCase, manualCase, CaseWithSteps(item, "SHOP-104", "Valid", "{\"steps\":[]}")),
        ]);
        broken.IsSuccess.ShouldBeFalse();
        broken.Error!.Code.ShouldBe(Evidgate.BuildingBlocks.SharedKernel.Api.ApiErrorCode.RequestInvalid);
        broken.Error.Details!.ShouldContain(detail => detail.Path == "/changes/0/content/steps");
    }

    [Fact]
    public async Task Steps_Leaves_AreStoredNfcComposed()
    {
        await using var world = await StartAsync();
        var (_, _, _, item) = await SeedLadderAsync(world);

        // The action carries "café" decomposed: e + U+0301. AC14 stores the composed spelling.
        var manualCase = Guid.CreateVersion7();
        var created = await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Create(
                AuthoringEntityKind.ManualTestCase,
                manualCase,
                CaseWithSteps(item, "SHOP-105", "NFC", "{\"steps\":[{\"no\":1,\"action\":\"Tap caf\u0065\u0301\",\"expected\":\"ok\"}]}")),
        ]);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var stored = await _fixture.ScalarAsync<string>(
            world.Database,
            "select steps::text from public.manual_test_case where id = '" + manualCase.ToString("D") + "'");
        stored.ShouldNotBeNull();
        stored!.Contains("caf\u00e9", StringComparison.Ordinal).ShouldBeTrue("the composed spelling is stored");
        stored.Contains('\u0301').ShouldBeFalse("no combining mark survives the write");
    }

    /// <summary>The module → feature → category → item ladder every case test needs, created in one batch.</summary>
    private static async Task<(Guid Module, Guid Feature, Guid Category, Guid Item)> SeedLadderAsync(World world)
    {
        var module = Guid.CreateVersion7();
        var feature = Guid.CreateVersion7();
        var category = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();

        (await AppendAsync(world, Guid.CreateVersion7(), [
            EntityChange.Create(AuthoringEntityKind.FeatureNode, module, NodeContent(FeatureNodeKinds.Module, "Shop", 0, null)),
            EntityChange.Create(AuthoringEntityKind.FeatureNode, feature, NodeContent(FeatureNodeKinds.Feature, "Checkout", 0, module)),
            EntityChange.Create(AuthoringEntityKind.FeatureNode, category, NodeContent(FeatureNodeKinds.Category, "Payment", 0, feature)),
            EntityChange.Create(AuthoringEntityKind.ChecklistItem, item, ItemContent(category, "Pay", 0)),
        ])).IsSuccess.ShouldBeTrue();

        return (module, feature, category, item);
    }

    // ------------------------------------------------------------------ helpers

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonElement NodeContent(string kind, string title, int position, Guid? parentId) => Json(
        "{\"kind\":\"" + kind + "\",\"title\":\"" + title + "\",\"position\":" + position.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"parent_id\":" + (parentId is { } parent ? "\"" + parent.ToString("D") + "\"" : "null") + "}");

    private static JsonElement ItemContent(Guid nodeId, string title, int position, string state = ChecklistItemStateMachine.Active) => Json(
        "{\"feature_node_id\":\"" + nodeId.ToString("D") + "\",\"title\":\"" + title
        + "\",\"description\":null,\"priority\":\"high\",\"state\":\"" + state + "\",\"position\":"
        + position.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");

    private static JsonElement CaseContent(Guid itemId, string code, string title, string state = ManualTestCaseStateMachine.Draft) => Json(
        "{\"checklist_item_id\":\"" + itemId.ToString("D") + "\",\"code\":\"" + code + "\",\"title\":\"" + title
        + "\",\"description\":null,\"precondition\":null,"
        + "\"steps\":{\"steps\":[{\"no\":1,\"action\":\"Open the cart\",\"expected\":\"The cart is shown\"}]},"
        + "\"priority\":\"high\",\"state\":\"" + state + "\"}");

    private static JsonElement CaseWithSteps(Guid itemId, string code, string title, string stepsDocument) => Json(
        "{\"checklist_item_id\":\"" + itemId.ToString("D") + "\",\"code\":\"" + code + "\",\"title\":\"" + title
        + "\",\"description\":null,\"precondition\":null,\"steps\":" + stepsDocument
        + ",\"priority\":\"high\",\"state\":\"" + ManualTestCaseStateMachine.Draft + "\"}");

    private static async Task<Result<AuthoringAppendOutcome>> AppendAsync(
        World world,
        Guid batchId,
        IReadOnlyList<EntityChange> changes)
    {
        using var services = world.Provider.CreateScope();
        var factory = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await factory.BeginAsync(world.Org, world.Project, false, TestContext.Current.CancellationToken);
        var writer = services.ServiceProvider.GetRequiredService<AuthoringVersionWriter>();
        var result = await writer.AppendAsync(
            world.Org,
            world.Project,
            batchId,
            null,
            changes,
            null,
            TestContext.Current.CancellationToken);

        if (result.IsSuccess)
        {
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        return result;
    }

    private static async Task<Result<AuthoringMoveOutcome>> MoveAsync(
        World world,
        Guid caseId,
        Guid toItemId,
        string? reason)
    {
        using var services = world.Provider.CreateScope();
        var factory = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await factory.BeginAsync(world.Org, world.Project, false, TestContext.Current.CancellationToken);
        var writer = services.ServiceProvider.GetRequiredService<AuthoringVersionWriter>();
        var result = await writer.MoveManualTestCaseAsync(
            world.Org,
            world.Project,
            Guid.CreateVersion7(),
            null,
            caseId,
            toItemId,
            reason,
            null,
            TestContext.Current.CancellationToken);

        if (result.IsSuccess)
        {
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        return result;
    }

    private static async Task<Result<AuthoringVersionRef>> RestoreAsync(
        World world,
        AuthoringEntityKind kind,
        Guid entityId)
    {
        using var services = world.Provider.CreateScope();
        var factory = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await factory.BeginAsync(world.Org, world.Project, false, TestContext.Current.CancellationToken);
        var writer = services.ServiceProvider.GetRequiredService<AuthoringVersionWriter>();
        var result = await writer.RestoreAsync(
            world.Org,
            world.Project,
            Guid.CreateVersion7(),
            null,
            kind,
            entityId,
            expectedVersion: null,
            reason: "restore",
            fromVersionId: null,
            context: null,
            TestContext.Current.CancellationToken);

        if (result.IsSuccess)
        {
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        return result;
    }

    private static async Task<Result<AuthoringCode>> AllocateAsync(World world, bool commit)
    {
        using var services = world.Provider.CreateScope();
        var factory = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await factory.BeginAsync(world.Org, world.Project, false, TestContext.Current.CancellationToken);
        var allocator = services.ServiceProvider.GetRequiredService<AuthoringCodeAllocator>();
        var result = await allocator.AllocateAsync(world.Org, world.Project, TestContext.Current.CancellationToken);

        if (result.IsSuccess && commit)
        {
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        return result;
    }

    private static async Task<Result<AuthoringCode>> AllocateWithAsync(ServiceProvider provider, World world, bool commit)
    {
        using var services = provider.CreateScope();
        var factory = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await factory.BeginAsync(world.Org, world.Project, false, TestContext.Current.CancellationToken);
        var allocator = services.ServiceProvider.GetRequiredService<AuthoringCodeAllocator>();
        var result = await allocator.AllocateAsync(world.Org, world.Project, TestContext.Current.CancellationToken);

        if (result.IsSuccess && commit)
        {
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        return result;
    }
}
