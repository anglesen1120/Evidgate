using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Ingestion.Application;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ProducerSlotQaTests
{
    // ------------------------------------------------------------------ privilege (app role, migration 0029 grants)

    [Fact]
    public async Task AppRole_CannotUpdateShardIndex()
    {
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync(
            "update public.producer_slot set shard_index = 2 where run_id = @run",
            ("run", run));

        error.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task AppRole_CannotDeleteSlots()
    {
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync("delete from public.producer_slot where run_id = @run", ("run", run));

        error.SqlState.ShouldBe("42501");
    }

    // ------------------------------------------------------------------ ciRunRef identity (documented safe default)

    [Fact]
    public async Task BindOrCheck_NullCiRunRefThenCiRunRef_IsTaken()
    {
        // The comparison is exact, so adding a CI reference to a slot first bound without one is a
        // different identity: producer_slot_taken, never a silent re-bind (BindOrCheckAsync XML doc).
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));
        await InScopeAsync(world, r => r.BindOrCheckAsync(run, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));

        var retry = await InScopeAsync(world, r => r.BindOrCheckAsync(run, "shard-1", "jti-a", "1/7/2", TestContext.Current.CancellationToken));

        retry.IsSuccess.ShouldBeFalse();
        retry.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        retry.Error.Status.ShouldBe(403);
    }

    [Fact]
    public async Task BindOrCheck_CiRunRefThenNull_IsTaken()
    {
        // The other direction: dropping the CI reference is equally a different identity.
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 1, TestContext.Current.CancellationToken));
        await InScopeAsync(world, r => r.BindOrCheckAsync(run, "shard-1", "jti-a", "1/7/2", TestContext.Current.CancellationToken));

        var retry = await InScopeAsync(world, r => r.BindOrCheckAsync(run, "shard-1", "jti-a", null, TestContext.Current.CancellationToken));

        retry.IsSuccess.ShouldBeFalse();
        retry.Error!.Code.ShouldBe(ApiErrorCode.ProducerSlotTaken);
        retry.Error.Status.ShouldBe(403);
    }

    // ------------------------------------------------------------------ plan §8 P4/P3: the lookups are indexed

    [Fact]
    public async Task BindAndSlotRead_UseTheirIndexes()
    {
        // P4/P3: the bind lookup is by (org_id, run_id, producer_id) and the per-run slot read is by
        // (org_id, run_id). Sequential scans are disabled in this session (the WaiverTests precedent,
        // tests/Tenancy.IntegrationTests/WaiverTests.cs:504) so a three-row table cannot make the
        // planner prefer a scan, and the production statements are explained with their real parameters.
        //
        // Which index serves which query:
        //   - BindIfUnbound / ReadByProducer filter on (org_id, run_id, producer_id), so the planner
        //     picks producer_slot_run_producer_key - the unique that carries exactly that column list.
        //   - ReadRunSlots filters on (org_id, run_id) and orders by shard_index. The planner picks
        //     producer_slot_run_shard_key (org_id, run_id, shard_index): one index serves both the filter and
        //     the order. producer_slot_run (org_id, run_id) is the plan §4 index and is kept for that prefix;
        //     the planner does not choose it for this query.
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 3, TestContext.Current.CancellationToken));
        await _fixture.ExecuteAsync(world.Database, "analyze public.producer_slot");

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        await scope.SetAsync("enable_seqscan", "off");

        var bind = await scope.ScalarAsync<string>(
            $"explain (format json) {ProducerSlotSql.BindIfUnbound}",
            ("org_id", world.Org),
            ("run_id", run),
            ("producer_id", "shard-1"),
            ("jti", "jti-a"),
            ("ci_run_ref", "1/7/2"));
        IndexNames(bind).ShouldBe(["producer_slot_run_producer_key"]);
        bind.ShouldNotContain("Seq Scan");

        var read = await scope.ScalarAsync<string>(
            $"explain (format json) {ProducerSlotSql.ReadRunSlots}",
            ("org_id", world.Org),
            ("run_id", run));
        IndexNames(read).ShouldNotBeEmpty();
        IndexNames(read).ShouldAllBe(name => name == "producer_slot_run" || name == "producer_slot_run_shard_key");
        read.ShouldNotContain("Seq Scan");
    }

    [Fact]
    public async Task ReadByProducerAndCloseIfOpen_UseTheirIndexes()
    {
        // P3/P4: ReadByProducer is the single indexed lookup of BindOrCheckAsync (and the read CloseSlotAsync
        // decides on). It carries the T88 run join, so the run's CI reference is compared in the same
        // statement; that join must reach the run primary key. CloseIfOpen is the one-shot finalize. Both
        // filter on (org_id, run_id, producer_id), so with sequential scans disabled (the WaiverTests
        // precedent) the planner must reach producer_slot_run_producer_key for the slot row.
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 3, TestContext.Current.CancellationToken));
        await _fixture.ExecuteAsync(world.Database, "analyze public.producer_slot");
        await _fixture.ExecuteAsync(world.Database, "analyze public.run");

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        await scope.SetAsync("enable_seqscan", "off");

        var read = await scope.ScalarAsync<string>(
            $"explain (format json) {ProducerSlotSql.ReadByProducer}",
            ("org_id", world.Org),
            ("run_id", run),
            ("producer_id", "shard-1"));
        var readIndexes = IndexNames(read);
        readIndexes.ShouldContain("producer_slot_run_producer_key");
        // The T88 run join is served by a run index. With a single run row the planner is free to pick
        // either run_org_id_key (the (org_id, id) unique) or run_project_created_idx; what matters is that
        // the run side is an index access at all, never a sequential scan.
        readIndexes.ShouldContain(name => name.StartsWith("run_", StringComparison.Ordinal));
        read.ShouldNotContain("Seq Scan");

        var close = await scope.ScalarAsync<string>(
            $"explain (format json) {ProducerSlotSql.CloseIfOpen}",
            ("org_id", world.Org),
            ("run_id", run),
            ("producer_id", "shard-1"),
            ("jti", "jti-a"),
            ("ci_run_ref", "1/7/2"),
            ("declared_last_sequence", 3L),
            ("declared_count", 3));
        IndexNames(close).ShouldContain("producer_slot_run_producer_key");
        close.ShouldNotContain("Seq Scan");
    }

    /// <summary>Every <c>Index Name</c> in an <c>EXPLAIN (FORMAT JSON)</c> plan, in plan order.</summary>
    private static List<string> IndexNames(string plan)
    {
        var names = new List<string>();
        Collect(JsonDocument.Parse(plan).RootElement, names);

        return names;

        static void Collect(JsonElement element, List<string> names)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.NameEquals("Index Name"))
                        {
                            names.Add(property.Value.GetString()!);
                        }
                        else
                        {
                            Collect(property.Value, names);
                        }
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Collect(item, names);
                    }

                    break;
            }
        }
    }
}
