using System.Text.Json;
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
            ("owner_id", OwnerA));
        PlanValues(bind, "Index Name").ShouldBe(["producer_slot_run_producer_key"]);
        bind.ShouldNotContain("Seq Scan");

        var read = await scope.ScalarAsync<string>(
            $"explain (format json) {ProducerSlotSql.ReadRunSlots}",
            ("org_id", world.Org),
            ("run_id", run));
        PlanValues(read, "Index Name").ShouldNotBeEmpty();
        PlanValues(read, "Index Name").ShouldAllBe(name => name == "producer_slot_run" || name == "producer_slot_run_shard_key");
        read.ShouldNotContain("Seq Scan");
    }

    [Fact]
    public async Task ReadByProducerAndCloseIfOpen_UseTheirIndexes()
    {
        // P3/P4: ReadByProducer is the single indexed lookup of BindOrCheckAsync (and the read CloseSlotAsync
        // decides on); CloseIfOpen is the one-shot finalize. Both filter on (org_id, run_id, producer_id), so
        // with sequential scans disabled (the WaiverTests precedent) the planner must reach
        // producer_slot_run_producer_key. Neither statement reads the run table any more.
        await using var world = await StartAsync();
        var run = await CreateRunAsync(world);
        await InScopeAsync(world, r => r.RegisterSlotsAsync(run, 3, TestContext.Current.CancellationToken));
        await _fixture.ExecuteAsync(world.Database, "analyze public.producer_slot");

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        await scope.SetAsync("enable_seqscan", "off");

        var read = await scope.ScalarAsync<string>(
            $"explain (format json) {ProducerSlotSql.ReadByProducer}",
            ("org_id", world.Org),
            ("run_id", run),
            ("producer_id", "shard-1"));
        PlanValues(read, "Index Name").ShouldBe(["producer_slot_run_producer_key"]);
        PlanValues(read, "Relation Name").ShouldBe(["producer_slot"]);
        read.ShouldNotContain("Seq Scan");

        var close = await scope.ScalarAsync<string>(
            $"explain (format json) {ProducerSlotSql.CloseIfOpen}",
            ("org_id", world.Org),
            ("run_id", run),
            ("producer_id", "shard-1"),
            ("owner_id", OwnerA),
            ("declared_last_sequence", 3L),
            ("declared_count", 3));
        PlanValues(close, "Index Name").ShouldBe(["producer_slot_run_producer_key"]);
        close.ShouldNotContain("Seq Scan");
    }

    /// <summary>Every value of <paramref name="property"/> in an <c>EXPLAIN (FORMAT JSON)</c> plan, in plan order.</summary>
    private static List<string> PlanValues(string plan, string property)
    {
        var values = new List<string>();
        Collect(JsonDocument.Parse(plan).RootElement, property, values);

        return values;

        static void Collect(JsonElement element, string property, List<string> values)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var member in element.EnumerateObject())
                    {
                        if (member.NameEquals(property))
                        {
                            values.Add(member.Value.GetString()!);
                        }
                        else
                        {
                            Collect(member.Value, property, values);
                        }
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Collect(item, property, values);
                    }

                    break;
            }
        }
    }
}
