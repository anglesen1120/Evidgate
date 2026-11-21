using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringVersionTests
{
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
}
