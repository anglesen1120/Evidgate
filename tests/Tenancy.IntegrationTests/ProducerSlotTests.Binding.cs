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
    // ------------------------------------------------------------------ (e) binding (T9)

    [Fact]
    public async Task BindOrCheck_UnknownProducer_422()
    {
        await using var world = await StartAsync();

        var result = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(Guid.CreateVersion7(), "shard-1", "jti-a", null, TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.UnknownProducer);
        result.Error.Status.ShouldBe(422);
    }

    [Fact]
    public async Task BindOrCheck_FirstBind_SetsJti_ThenSameIdentityIsIdempotent()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));

        var first = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-a", "1/7/2", TestContext.Current.CancellationToken));
        var second = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-a", "1/7/2", TestContext.Current.CancellationToken));

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        first.Value.Bound.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue(second.Error?.Message);
        second.Value.Bound.ShouldBeFalse();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ScalarAsync<string>("select bound_token_jti from public.producer_slot")).ShouldBe("jti-a");
        (await scope.ScalarAsync<string>("select bound_ci_run_ref from public.producer_slot")).ShouldBe("1/7/2");
    }

    [Fact]
    public async Task BindOrCheck_ForeignJti_403()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));
        await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));

        var foreign = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-b", null, TestContext.Current.CancellationToken));

        foreign.IsSuccess.ShouldBeFalse();
        foreign.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        foreign.Error.Status.ShouldBe(403);
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ScalarAsync<string>("select bound_token_jti from public.producer_slot")).ShouldBe("jti-a");
    }

    [Fact]
    public async Task binds_then_foreign_identity_403()
    {
        // The plan's AC3 row: the FIRST write binds the slot to the caller's jti, and a foreign jti is then
        // refused with 403 producer_slot_taken - the same answer a foreign CI run gets (T88), so neither
        // learns more than that code about the slot (T9).
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));

        var bound = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));
        var foreign = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-b", null, TestContext.Current.CancellationToken));

        bound.IsSuccess.ShouldBeTrue(bound.Error?.Message);
        bound.Value.Bound.ShouldBeTrue();
        foreign.IsSuccess.ShouldBeFalse();
        foreign.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        foreign.Error.Status.ShouldBe(403);
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ScalarAsync<string>("select bound_token_jti from public.producer_slot")).ShouldBe("jti-a");
    }

    [Fact]
    public async Task BindOrCheck_CiRunRefMismatch_403()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));
        await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-a", "1/7/2", TestContext.Current.CancellationToken));

        var mismatch = await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-a", "1/7/3", TestContext.Current.CancellationToken));

        mismatch.IsSuccess.ShouldBeFalse();
        mismatch.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        mismatch.Error.Status.ShouldBe(403);
    }

    [Fact]
    public async Task BindOrCheck_ConcurrentFirstBinders_ExactlyOneWins()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 1, TestContext.Current.CancellationToken));

        // Two first writers race for the same unbound slot: the conditional UPDATE is the arbiter, so
        // exactly one binds and the other is told the slot is taken (T9).
        var first = InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));
        var second = InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-1", "jti-b", null, TestContext.Current.CancellationToken));

        var results = await Task.WhenAll(first, second);

        results.Count(result => result.IsSuccess && result.Value.Bound).ShouldBe(1);
        results.Count(result => !result.IsSuccess && result.Error!.Code == ApiErrorCode.ProducerSlotTaken).ShouldBe(1);
    }

    [Fact]
    public async Task GetRunProducerSlots_ReturnsTheRunsSlots()
    {
        await using var world = await StartAsync();
        var runId = await CreateRunAsync(world);
        await InScopeAsync(world, world.Org, registry => registry.RegisterSlotsAsync(runId, 2, TestContext.Current.CancellationToken));
        await InScopeAsync(world, world.Org, registry =>
            registry.BindOrCheckAsync(runId, "shard-2", "jti-a", null, TestContext.Current.CancellationToken));

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
        // T4.1: the entity and its configuration are the module's mapping of migration 0029, so a row
        // written through the EF context must read back identically through EF and through raw SQL -
        // the version default, the trigger-owned timestamps and the nullable binding columns included.
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

            var read = await context.ProducerSlots.SingleAsync(row => row.Id == id, TestContext.Current.CancellationToken);
            read.ShardIndex.ShouldBe(1);
            read.ProducerId.ShouldBe("shard-1");
            read.Version.ShouldBe(1);
            read.LastSequence.ShouldBe(0);
            read.BoundTokenJti.ShouldBeNull();
            read.BoundCiRunRef.ShouldBeNull();
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
