using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Ingestion.Domain;
using Evidgate.Modules.Ingestion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ProducerSlotTests
{
    private const string OwnerA = "0192ee53-41fe-7000-8000-000000000001";
    private const string OwnerB = "0192ee53-41fe-7000-8000-000000000002";

    [Fact]
    public async Task BindOrCheck_UnknownProducer_422()
    {
        await using var world = await StartAsync();

        var result = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(Guid.CreateVersion7(), "shard-1", OwnerA, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.UnknownProducer);
        result.Error.Status.ShouldBe(422);
    }

    [Fact]
    public async Task BindOrCheck_FirstBind_PersistsOwnerAndSameOwnerRetryIsIdempotent()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));

        var first = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", OwnerA, TestContext.Current.CancellationToken));
        var second = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", OwnerA, TestContext.Current.CancellationToken));

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        first.Value.Bound.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue(second.Error?.Message);
        second.Value.Bound.ShouldBeFalse();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ScalarAsync<string>("select bound_token_jti from public.producer_slot")).ShouldBe(OwnerA);
        (await scope.ScalarAsync<bool>("select bound_ci_run_ref is null from public.producer_slot")).ShouldBeTrue();
    }

    [Fact]
    public async Task BindOrCheck_ForeignOwner_Returns403AndKeepsOriginalOwner()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));
        var first = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", OwnerA, TestContext.Current.CancellationToken));
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        first.Value.Bound.ShouldBeTrue();

        var foreign = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", OwnerB, TestContext.Current.CancellationToken));

        foreign.IsSuccess.ShouldBeFalse();
        foreign.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        foreign.Error.Status.ShouldBe(403);
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ScalarAsync<string>("select bound_token_jti from public.producer_slot")).ShouldBe(OwnerA);
    }

    [Fact]
    public async Task BindAndClose_LegacyReferencePresent_OnlyStoredOwnerCanRetryAndClose()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await using (var seed = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            // Rows written before owner-only binding can retain the unused reference.
            (await seed.ExecuteAsync(
                """
                insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id, bound_token_jti, bound_ci_run_ref)
                values (@id, @org, @run, 1, 'shard-1', @owner, '1/7/2')
                """,
                ("id", Guid.CreateVersion7()), ("org", world.Org), ("run", runId), ("owner", OwnerA))).ShouldBe(1);
            await seed.CommitAsync();
        }

        var ct = TestContext.Current.CancellationToken;
        var retry = await InScopeAsync(world, world.Org, registry => registry.BindOrCheckAsync(runId, "shard-1", OwnerA, ct));
        retry.IsSuccess.ShouldBeTrue(retry.Error?.Message);
        retry.Value.Bound.ShouldBeFalse();
        var foreign = await InScopeAsync(world, world.Org, registry => registry.BindOrCheckAsync(runId, "shard-1", OwnerB, ct));
        foreign.IsSuccess.ShouldBeFalse();
        foreign.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        foreign.Error.Status.ShouldBe(403);

        var denied = await InScopeAsync(world, world.Org, registry => registry.CloseSlotAsync(runId, "shard-1", OwnerB, 3, 2, ct));
        denied.IsSuccess.ShouldBeFalse();
        denied.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        denied.Error.Status.ShouldBe(403);
        var closed = await InScopeAsync(world, world.Org, registry => registry.CloseSlotAsync(runId, "shard-1", OwnerA, 3, 2, ct));
        closed.IsSuccess.ShouldBeTrue(closed.Error?.Message);
        closed.Value.Closed.ShouldBeTrue();
        var replay = await InScopeAsync(world, world.Org, registry => registry.CloseSlotAsync(runId, "shard-1", OwnerA, 8, 7, ct));
        replay.IsSuccess.ShouldBeTrue(replay.Error?.Message);
        replay.Value.Closed.ShouldBeFalse();
        replay.Value.ClosedAt.ShouldBe(closed.Value.ClosedAt);
        replay.Value.DeclaredLastSequence.ShouldBe(3);
        replay.Value.DeclaredCount.ShouldBe(2);
    }

    [Fact]
    public async Task BindOrCheck_ConcurrentFirstBinders_ExactlyOneWins()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));

        // A losing conditional update must not replace the winner's owner ID.
        var first = InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", OwnerA, TestContext.Current.CancellationToken));
        var second = InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", OwnerB, TestContext.Current.CancellationToken));

        var results = await Task.WhenAll(first, second);

        results.Count(result => result.IsSuccess && result.Value.Bound).ShouldBe(1);
        results.Count(result => !result.IsSuccess && result.Error!.Code == ApiErrorCode.ProducerSlotTaken).ShouldBe(1);
        var winner = results[0].IsSuccess ? OwnerA : OwnerB;
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ScalarAsync<string>("select bound_token_jti from public.producer_slot")).ShouldBe(winner);
    }

    [Fact]
    public async Task GetRunProducerSlots_ReturnsTheRunsSlots()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 2, TestContext.Current.CancellationToken));
        await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-2", OwnerA, TestContext.Current.CancellationToken));

        var slots = await InScopeAsync(
            world, world.Org, registry => registry.GetRunProducerSlotsAsync(runId, TestContext.Current.CancellationToken));

        slots.Count.ShouldBe(2);
        slots[0].ShardIndex.ShouldBe(1);
        slots[0].Bound.ShouldBeFalse();
        slots[0].ClosedAt.ShouldBeNull();
        slots[0].LastSequence.ShouldBe(0);
        slots[1].ShardIndex.ShouldBe(2);
        slots[1].Bound.ShouldBeTrue();
    }

    [Fact]
    public async Task GetRunProducerSlots_OtherOrg_ReturnsEmpty()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));
        var otherOrg = await CreateOtherOrgAsync(world, "other");

        var slots = await InScopeAsync(
            world, otherOrg, registry => registry.GetRunProducerSlotsAsync(runId, TestContext.Current.CancellationToken));

        slots.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ (f) the EF write model (T4.1)

    [Fact]
    public async Task WriteModel_RoundTripsThroughEfAndRawSql()
    {
        // Read back from PostgreSQL rather than using the instance saved in the context.
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        var id = Guid.CreateVersion7();

        await using (var services = world.Api.Services.CreateAsyncScope())
        {
            await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
                .BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
            await using var context = services.ServiceProvider
                .GetRequiredService<ITenantDbContextFactory<IngestionDbContext>>().Create();

            await context.ProducerSlots.AddAsync(new ProducerSlot(id, world.Org, runId, 1, "shard-1"), TestContext.Current.CancellationToken);
            (await context.SaveChangesAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

            var read = await context.ProducerSlots.AsNoTracking().SingleAsync(row => row.Id == id, TestContext.Current.CancellationToken);
            read.ShardIndex.ShouldBe(1);
            read.ProducerId.ShouldBe("shard-1");
            read.Version.ShouldBe(1);
            read.LastSequence.ShouldBe(0);
            read.BoundTokenJti.ShouldBeNull();
            read.ClosedAt.ShouldBeNull();
            read.CreatedAt.ShouldNotBe(default);

            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        await using var raw = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await raw.ScalarAsync<long>("select count(*) from public.producer_slot")).ShouldBe(1);
        (await raw.ScalarAsync<int>("select version from public.producer_slot")).ShouldBe(1);
        (await raw.ScalarAsync<string>("select producer_id from public.producer_slot")).ShouldBe("shard-1");
        (await raw.ScalarAsync<long>("select last_sequence from public.producer_slot")).ShouldBe(0);
    }
}
