// 12.3.1-T4.4: IQuotaBlockStamp after the ingest rollback — the counter a refusal at a month's first call has to
// create, and the already-stamped counter a repeat refusal must leave unlocked. The lock case is the regression guard
// for the stamp's predicate placement: a stamp that locked the row it then filters out would queue behind every
// concurrent refusal of the scope, and no xmin comparison can show a lock.
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <see cref="IQuotaBlockStamp"/> against a real PostgreSQL: the carried counter a refusal at a month's first call
/// leaves behind, and the stamped counter a repeat refusal answers without waiting for anybody's lock.
/// </summary>
[Trait("Category", "Docker")]
public sealed class UsageQuotaStampTests(PostgresTenancyFixture fixture)
{
    private const string Period = "2026-10";
    private const string PreviousPeriod = "2026-09";
    private const decimal WarnRatio = 0.8m;

    [Fact]
    public async Task Stamp_AlreadyStampedCounter_ReturnsWhileAnotherTransactionHoldsTheRow()
    {
        await using var world = await UsageQuotaWorld.StartAsync(fixture);

        // Already stamped and still without room, so the stamp has nothing to write for this counter.
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), world.Project, Period, "results", 10m, 10m, WarnRatio,
            BlockedAt: DateTimeOffset.UtcNow));
        var before = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();

        // Another refusal of the same scope, still inside its transaction: it holds the row a locking stamp wants.
        await using var holder = await world.BeginAppScopeAsync();
        (await holder.ScalarAsync<Guid>(
            "select id from public.quota_counter where org_id = @org_id and project_id = @project_id "
            + "and period = @period and metric = 'results' for update",
            ("org_id", world.Org),
            ("project_id", world.Project),
            ("period", Period))).ShouldNotBe(Guid.Empty, "the holder's row is there to be locked");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));

        // A stamp that locked the row its predicate then filters out would queue here — the holder never commits inside
        // this test — so the deadline turns that queue into a failure instead of a hung suite.
        await Should.NotThrowAsync(() => StampAsync(world, quantity: 1m, deadline.Token));

        var after = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        after.ShouldBe(before, "a repeat refusal writes nothing: no new version and no second stamp");
    }

    [Fact]
    public async Task Stamp_FirstCallOfAMonth_CreatesTheCarriedCounterAndStampsIt()
    {
        await using var world = await UsageQuotaWorld.StartAsync(fixture);

        // The month has no counter; the previous one left a limit of one, which the refused quantity of two exceeds.
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), world.Project, PreviousPeriod, "results", 0m, 1m, WarnRatio));

        await StampAsync(world, quantity: 2m, TestContext.Current.CancellationToken);

        var carried = (await world.CounterAsync(project: world.Project, period: Period)).ShouldNotBeNull();
        carried.Used.ShouldBe(0m, "the stamp reports a refusal; it never charges the units it reports");
        carried.Limit.ShouldBe(1m, "the limit carried from the previous month");
        carried.BlockedAt.ShouldNotBeNull("and the refusal is stamped on the counter the stamp created");
    }

    /// <summary>Runs the stamp the ingest route runs after its rollback, in a tenant scope of its own.</summary>
    /// <param name="world">The world the counter belongs to.</param>
    /// <param name="quantity">The refused call's quantity, which the stamp's predicate and period both validate.</param>
    /// <param name="cancellationToken">The deadline the caller allows the stamp.</param>
    private static async Task StampAsync(
        UsageQuotaWorld world,
        decimal quantity,
        CancellationToken cancellationToken)
    {
        await using var services = world.Api.Services.CreateAsyncScope();

        await services.ServiceProvider
            .GetRequiredService<IQuotaBlockStamp>()
            .StampAsync(
                world.Org,
                new QuotaCheckRequest(UsageMetric.Results, Period, world.Project, quantity),
                cancellationToken);
    }
}
