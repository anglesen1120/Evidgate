using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The counter's batch and reservation members of 13.1.1 §4 (13.1.1-T5, OI-13-107): <c>AllocateManyAsync</c> hands out a
/// block of consecutive numbers with one statement and <c>ReserveAtLeastAsync</c> raises the counter past a number an
/// import brought with it, never lowering it. Both keep <c>AllocateAsync</c>'s design — own connection, own transaction,
/// own commit — so the interesting properties are the ones one allocation cannot show: blocks never overlap, a block
/// costs the same one statement whatever its size, and the 10⁹ cap refuses a block that would leave the range without
/// writing anything.
/// </summary>
/// <remarks>
/// The allocator is driven directly rather than through a route: it is the import's support member (13.1.1-T5.1) and its
/// own connection is the subject of the test. The tenant scope comes from the host's real
/// <see cref="ITenantScopeFactory"/>, so the allocator's "the open scope's organization" guard runs exactly as it does
/// inside a request.
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuthoringAllocateManyTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuthoringAllocateManyTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>Runs <paramref name="call"/> on the host's allocator in one writable tenant scope of the world.</summary>
    private static async Task<T> InScopeAsync<T>(
        World world,
        Func<AuthoringCodeAllocator, CancellationToken, ValueTask<T>> call)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, world.Project, false, cancellationToken);

        var allocator = services.ServiceProvider.GetRequiredService<AuthoringCodeAllocator>();
        var result = await call(allocator, cancellationToken);
        await scope.CommitAsync(cancellationToken);

        return result;
    }

    /// <summary>The project's stored <c>case_prefix</c> (the insert trigger derived it from the slug).</summary>
    private Task<string> StoredPrefixAsync(World world) => _fixture.ScalarAsync<string>(
        world.Database,
        $"select case_prefix from public.project where id = '{world.Project:D}'");

    /// <summary>The counter row's <c>next_value</c>, or null when no allocation ran yet.</summary>
    private Task<long?> CounterAsync(World world) => _fixture.ScalarAsync<long?>(
        world.Database,
        $"select next_value from public.authoring_code_counter where org_id = '{world.Org:D}'"
        + $" and project_id = '{world.Project:D}'");

    /// <summary>
    /// Moves the counter to <paramref name="nextValue"/> as the tenant owner role, which the table's
    /// <c>authoring_code_counter_owner_backfill</c> policy admits (0039:506-508): the app role may only bump
    /// <c>next_value</c> from where it is, and the top of the range is reached by seeding instead of a million calls.
    /// </summary>
    private async Task SeedCounterAsync(World world, long nextValue)
    {
        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org);

        (await seed.ExecuteAsync(
            "update public.authoring_code_counter set next_value = @next_value"
            + " where org_id = @org_id and project_id = @project_id",
            ("next_value", nextValue),
            ("org_id", world.Org),
            ("project_id", world.Project))).ShouldBe(1);

        await seed.CommitAsync();
    }

    /// <summary>
    /// A block of five is five consecutive numbers, formatted by <see cref="AuthoringCode"/>, and the single allocation
    /// that follows it continues after the block: one counter, one arithmetic, both members.
    /// </summary>
    [Fact]
    public async Task AllocateMany_ReturnsBlock_ThenAllocateAsyncContinues()
    {
        await using var world = await StartAsync();
        var prefix = await StoredPrefixAsync(world);
        var expectedNumbers = new long[] { 1L, 2L, 3L, 4L, 5L };
        var expectedCodes = new[]
        {
            prefix + "-001",
            prefix + "-002",
            prefix + "-003",
            prefix + "-004",
            prefix + "-005",
        };

        var (block, next) = await InScopeAsync(world, async (allocator, cancellationToken) =>
        {
            var many = await allocator.AllocateManyAsync(world.Org, world.Project, 5, cancellationToken);
            many.IsSuccess.ShouldBeTrue(many.Error?.Message);

            var single = await allocator.AllocateAsync(world.Org, world.Project, cancellationToken);
            single.IsSuccess.ShouldBeTrue(single.Error?.Message);

            return (many, single);
        });

        block.Value.Select(code => code.Number).ShouldBe(expectedNumbers);
        block.Value.Select(code => code.Value).ShouldBe(expectedCodes);

        // Every code is a formatted value object of the one grammar, and none of them repeats.
        block.Value.ShouldAllBe(code => code.Prefix == prefix);
        block.Value.Select(code => code.Value).Distinct().Count().ShouldBe(5);

        next.Value.Number.ShouldBe(6L);
        next.Value.Value.ShouldBe(prefix + "-006");

        (await CounterAsync(world)).ShouldBe(7L);
    }

    /// <summary>
    /// Two blocks allocated at the same time never overlap (AC4 under concurrency): the upsert's row lock serialises
    /// them, so the two calls together take exactly the first 100 numbers, each block consecutive in its own order.
    /// </summary>
    [Fact]
    public async Task AllocateMany_ConsecutiveAndNoOverlap()
    {
        await using var world = await StartAsync();
        var cancellationToken = TestContext.Current.CancellationToken;

        // One tenant scope per async flow — the factory's contract, and exactly how two importers race in production.
        // The rendezvous has two phases so the race is real and not a matter of scheduling: each flow parks on `go`
        // after announcing itself on `arrived`, and only this flow — after both announcements — releases them, so
        // neither block starts before the other is holding its own scope and waiting to allocate.
        using var arrived = new SemaphoreSlim(0, 2);
        using var go = new SemaphoreSlim(0, 2);

        async Task<long[]> AllocateInOwnScopeAsync()
        {
            await using var services = world.Api.Services.CreateAsyncScope();
            await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
                .BeginAsync(world.Org, world.Project, false, cancellationToken);

            var allocator = services.ServiceProvider.GetRequiredService<AuthoringCodeAllocator>();

            arrived.Release();
            await go.WaitAsync(cancellationToken);

            var allocated = await allocator.AllocateManyAsync(world.Org, world.Project, 50, cancellationToken);
            allocated.IsSuccess.ShouldBeTrue(allocated.Error?.Message);

            await scope.CommitAsync(cancellationToken);

            return [.. allocated.Value.Select(code => code.Number)];
        }

        var first = Task.Run(AllocateInOwnScopeAsync, cancellationToken);
        var second = Task.Run(AllocateInOwnScopeAsync, cancellationToken);

        // Two arrivals mean both flows hold a scope and are parked on `go`; releasing two permits starts both blocks
        // together, which is the only moment at which the counter's row lock decides the outcome.
        await arrived.WaitAsync(cancellationToken);
        await arrived.WaitAsync(cancellationToken);
        go.Release(2);

        var blocks = await Task.WhenAll(first, second);

        foreach (var block in blocks)
        {
            block.Length.ShouldBe(50);
            block.ShouldBe(Enumerable.Range((int)block[0], 50).Select(number => (long)number).ToArray());
        }

        blocks.SelectMany(block => block).Order()
            .ShouldBe(Enumerable.Range(1, 100).Select(number => (long)number).ToArray());
        (await CounterAsync(world)).ShouldBe(101L);
    }

    /// <summary>
    /// The reservation only ever raises: a code an import brought with it pushes the counter past its own number, and a
    /// later reservation below the counter leaves the next allocation where it was.
    /// </summary>
    [Fact]
    public async Task ReserveAtLeast_RaisesButNeverLowers()
    {
        await using var world = await StartAsync();

        var (raised, afterRaise, lowered, afterLower) = await InScopeAsync(
            world,
            async (allocator, cancellationToken) =>
            {
                var first = await allocator.AllocateManyAsync(world.Org, world.Project, 3, cancellationToken);
                first.IsSuccess.ShouldBeTrue(first.Error?.Message);
                first.Value[^1].Number.ShouldBe(3L);

                // The import brought number 100 with it: the next allocation must not hand out 4.
                var push = await allocator.ReserveAtLeastAsync(world.Org, world.Project, 100, cancellationToken);
                push.IsSuccess.ShouldBeTrue(push.Error?.Message);

                var above = await allocator.AllocateManyAsync(world.Org, world.Project, 2, cancellationToken);
                above.IsSuccess.ShouldBeTrue(above.Error?.Message);

                var below = await allocator.ReserveAtLeastAsync(world.Org, world.Project, 50, cancellationToken);
                below.IsSuccess.ShouldBeTrue(below.Error?.Message);

                var next = await allocator.AllocateAsync(world.Org, world.Project, cancellationToken);
                next.IsSuccess.ShouldBeTrue(next.Error?.Message);

                return (push, above, below, next);
            });

        var raisedFrom = new long[] { 100L, 101L };

        raised.Value.ShouldBeTrue();
        afterRaise.Value.Select(code => code.Number).ShouldBe(raisedFrom);
        lowered.Value.ShouldBeTrue();

        // 50 was below the counter, so that reservation wrote nothing and the next number is still 102.
        afterLower.Value.Number.ShouldBe(102L);
        (await CounterAsync(world)).ShouldBe(103L);
    }

    /// <summary>
    /// The 10⁹ cap (§4) refuses a block whole: one whose last number would leave the range writes nothing, the counter
    /// keeps the value it had, and the boundary that still fits keeps working. The sharp case is a block of <b>two</b> at
    /// a counter of 10⁹ — number 10⁹ itself is still allocatable, the block is not — because a guard that only refused a
    /// value past the cap would have written 10⁹ + 1 and answered the CHECK's 23514 as a 500 instead of
    /// <c>409 quota_exceeded</c>.
    /// </summary>
    [Fact]
    public async Task AllocateMany_AtTheCap_RefusesAndWritesNothing()
    {
        await using var world = await StartAsync();

        var seed = await InScopeAsync(world, (allocator, cancellationToken) =>
            allocator.AllocateManyAsync(world.Org, world.Project, 1, cancellationToken));
        seed.IsSuccess.ShouldBeTrue(seed.Error?.Message);

        // The last number a block may hold is 10⁹ (AuthoringCode.MaximumNumber), so from 999 999 999 a block of three
        // would end at 10⁹ + 1: one past the range, while a single allocation from the same counter still fits.
        await SeedCounterAsync(world, 999_999_999);

        var beforeCap = await InScopeAsync(world, async (allocator, cancellationToken) =>
        {
            var refusedBlock = await allocator.AllocateManyAsync(world.Org, world.Project, 3, cancellationToken);
            var counterAfterRefusal = await CounterAsync(world);

            var highest = await allocator.AllocateAsync(world.Org, world.Project, cancellationToken);

            // The sharpest case: number 10⁹ itself is still free, but a block of two from it is not — a guard that only
            // refused a value past the cap would have written 10⁹ + 1 and answered the CHECK's 23514 as a 500.
            var refusedAtCap = await allocator.AllocateManyAsync(world.Org, world.Project, 2, cancellationToken);
            var counterAtCap = await CounterAsync(world);

            return (refusedBlock, counterAfterRefusal, highest, refusedAtCap, counterAtCap);
        });

        beforeCap.refusedBlock.Status.ShouldBe(409);
        beforeCap.refusedBlock.Error!.Code.ShouldBe(ApiErrorCode.QuotaExceeded);
        beforeCap.counterAfterRefusal.ShouldBe(999_999_999L);

        beforeCap.highest.IsSuccess.ShouldBeTrue(beforeCap.highest.Error?.Message);
        beforeCap.highest.Value.Number.ShouldBe(999_999_999L);

        beforeCap.refusedAtCap.Status.ShouldBe(409);
        beforeCap.refusedAtCap.Error!.Code.ShouldBe(ApiErrorCode.QuotaExceeded);
        beforeCap.counterAtCap.ShouldBe(AuthoringCode.MaximumNumber);

        var atTheCap = await InScopeAsync(world, async (allocator, cancellationToken) =>
        {
            var lastNumber = await allocator.AllocateAsync(world.Org, world.Project, cancellationToken);
            var refusedOverflow = await allocator.AllocateManyAsync(world.Org, world.Project, 1, cancellationToken);

            // The last value the counter's CHECK allows (10⁹ + 1) is accepted; one above it is not.
            var atTheTop = await allocator.ReserveAtLeastAsync(
                world.Org,
                world.Project,
                AuthoringCode.MaximumNumber + 1,
                cancellationToken);
            var aboveTheTop = await allocator.ReserveAtLeastAsync(
                world.Org,
                world.Project,
                AuthoringCode.MaximumNumber + 2,
                cancellationToken);
            var counterAtTheEnd = await CounterAsync(world);

            return (lastNumber, refusedOverflow, atTheTop, aboveTheTop, counterAtTheEnd);
        });

        atTheCap.lastNumber.IsSuccess.ShouldBeTrue(atTheCap.lastNumber.Error?.Message);
        atTheCap.lastNumber.Value.Number.ShouldBe(AuthoringCode.MaximumNumber);

        atTheCap.refusedOverflow.Status.ShouldBe(409);
        atTheCap.refusedOverflow.Error!.Code.ShouldBe(ApiErrorCode.QuotaExceeded);

        atTheCap.atTheTop.IsSuccess.ShouldBeTrue(atTheCap.atTheTop.Error?.Message);
        atTheCap.atTheTop.Value.ShouldBeTrue();
        atTheCap.aboveTheTop.Status.ShouldBe(409);
        atTheCap.aboveTheTop.Error!.Code.ShouldBe(ApiErrorCode.QuotaExceeded);
        atTheCap.counterAtTheEnd.ShouldBe(AuthoringCode.MaximumNumber + 1);
    }

    /// <summary>
    /// The block's cost does not grow with its size: one statement against the counter either way (P2/P3's budget), and
    /// the same count over this module's two tables for two codes and for two hundred. The count is taken the way
    /// <c>AuthoringCommandCounterTests</c> takes it — the host's <see cref="StatementLog"/>, filtered to the statements
    /// that touch <c>authoring_code_counter</c> or the project row — so a connection's warm-up cannot decide it.
    /// </summary>
    [Fact]
    public async Task AllocateMany_StatementCount_DoesNotGrowWithTheBlock()
    {
        await using var world = await StartAsync();
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, world.Project, false, cancellationToken);
        var allocator = services.ServiceProvider.GetRequiredService<AuthoringCodeAllocator>();

        var small = await MeasureAsync(2);
        var large = await MeasureAsync(200);

        small.CounterStatements.ShouldBe(1);
        large.CounterStatements.ShouldBe(1);

        // One counter statement plus the project probe, whatever the block holds.
        small.ModuleStatements.ShouldBe(2);
        large.ModuleStatements.ShouldBe(small.ModuleStatements);

        // The one counter statement is the same text in both runs, so the block size travels as a parameter.
        large.CounterText.ShouldBe(small.CounterText);
        large.CounterText.ShouldContain("next_value = c.next_value + @n");

        await scope.CommitAsync(cancellationToken);

        async Task<Measured> MeasureAsync(int count)
        {
            world.Api.Statements.Clear();

            var allocated = await allocator.AllocateManyAsync(world.Org, world.Project, count, cancellationToken);
            allocated.IsSuccess.ShouldBeTrue(allocated.Error?.Message);

            var statements = world.Api.Statements.Snapshot();
            var module = statements
                .Where(statement => statement.Contains("authoring_code_counter", StringComparison.Ordinal)
                    || statement.Contains("public.project", StringComparison.Ordinal))
                .ToArray();
            var counter = module
                .Where(statement => statement.Contains("authoring_code_counter", StringComparison.Ordinal))
                .ToArray();

            return new Measured(module.Length, counter.Length, counter.Length == 1 ? counter[0] : string.Empty);
        }
    }

    /// <summary>What one measured block did, over this module's two tables.</summary>
    /// <param name="ModuleStatements">How many statements touched the counter or the project row.</param>
    /// <param name="CounterStatements">How many of those touched <c>authoring_code_counter</c>.</param>
    /// <param name="CounterText">That statement's text, when there is exactly one.</param>
    private sealed record Measured(int ModuleStatements, int CounterStatements, string CounterText);
}
