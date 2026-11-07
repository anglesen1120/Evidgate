using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Evidence.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportQuotaTests
{
    [Trait("Category", "Docker")]
    public sealed class QaS271DeferredBacklog(PostgresTenancyFixture fixture, SeaweedFsStore store)
        : IClassFixture<SeaweedFsStore>
    {
        private const int Backlog = 32;
        private const string Queue = "quota-backlog";
        private readonly EvidenceRawReportQuotaTests _subject = new(fixture, store);

        [Fact]
        public async Task DeferredReports_OneTenantBacklog_SpreadsWithinClampAndEveryTaskCanComplete()
        {
            World? current = null;
            var storage = new ReportStorageSpy();
            await using var world = await _subject.StartAsync(services =>
            {
                UseClock(services, new FakeTimeProvider(MidMarch));
                ReplaceObjectStorage(services, inner => storage.Wrap(inner));
                services.AddEvidgateTasks(new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["EVIDGATE:Tasks:BatchSize"] = Backlog.ToString(CultureInfo.InvariantCulture),
                    }).Build());
                services.AddTransactionlessTaskHandler<ParseRawReportPayload, ParseRawReportHandler>();
                services.AddSingleton<ParseRawReportHandler>(_ => CreateHandler(current
                    ?? throw new InvalidOperationException("the report world is not ready")));
            });
            current = world;
            await using var counters = await UsageQuotaWorld.StartAsync(fixture);
            await _subject.SeedPlanAsync(world);
            await _subject.SeedCounterAsync(counters, world, 1m, 1m);
            var ids = await SeedBacklogAsync(world);
            var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();

            (await consumer.PollOnceAsync(Queue, Token)).ShouldBe(Backlog);
            storage.Gets.ShouldBe(0, "every report was already deferred and the shared tenant still had no room");
            await AssertDeferredAsync(world, ids);
            (await consumer.PollOnceAsync(Queue, Token)).ShouldBe(0, "none is claimable ahead of its jittered deadline");

            (await _subject.RaiseAsync(counters, world, world.Project, Backlog + 1m)).IsSuccess.ShouldBeTrue();
            await fixture.ExecuteAsync(world.Database,
                $"update public.task set available_at = now() where org_id = '{world.Org:D}' and queue = '{Queue}'");
            (await consumer.PollOnceAsync(Queue, Token)).ShouldBe(Backlog);
            (await fixture.ScalarAsync<long>(world.Database,
                $"select count(*) from public.task where org_id = '{world.Org:D}' and queue = '{Queue}' and state = 'done'"))
                .ShouldBe(Backlog);
            (await TaskIdsAsync(world)).Order().ShouldBe(ids.Order());
            storage.Gets.ShouldBe(Backlog);
        }

        private async Task<Guid[]> SeedBacklogAsync(World world)
        {
            var ids = new Guid[Backlog];
            for (var index = 0; index < ids.Length; index++)
            {
                var artifact = await _subject.SeedArtifactAsync(world, OneEventBody);
                ids[index] = await _subject.SeedDeferredTaskAsync(world, artifact);
            }

            await fixture.ExecuteAsync(world.Database,
                $"update public.task set queue = '{Queue}' where org_id = '{world.Org:D}' and task_type = 'raw_report_parse'");
            return ids;
        }

        private async Task AssertDeferredAsync(World world, Guid[] ids)
        {
            var error = TaskDeferredException.PersistedError.Replace("'", "''", StringComparison.Ordinal);
            (await fixture.ScalarAsync<long>(world.Database, $"""
                select count(*) from public.task
                where org_id = '{world.Org:D}' and queue = '{Queue}' and state = 'retry'
                    and attempts = 0 and locked_until is null and last_error = '{error}'
                """)).ShouldBe(Backlog);
            (await TaskIdsAsync(world)).Order().ShouldBe(ids.Order());
            var seconds = (await fixture.ColumnAsync<string>(world.Database, $"""
                select extract(epoch from (available_at - updated_at))::text
                from public.task where org_id = '{world.Org:D}' and queue = '{Queue}'
                """)).Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            seconds.Length.ShouldBe(Backlog);
            seconds.ShouldAllBe(value => value >= 870 && value <= 900);
            seconds.Distinct().Count().ShouldBeGreaterThan(1, "the real handler must sample separately for the tenant's tasks");
        }

        private async Task<Guid[]> TaskIdsAsync(World world) =>
            [.. (await fixture.ColumnAsync<string>(world.Database,
                $"select id::text from public.task where org_id = '{world.Org:D}' and queue = '{Queue}'")).Select(Guid.Parse)];

        [Theory]
        [InlineData(900, 900)]
        [InlineData(30, 30)]
        [InlineData(1, 1)]
        public void ReportRetry_DeterministicBacklog_StaysInsideEveryClamp(int secondsBeforeMonthEnd, int ceiling)
        {
            var now = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(-secondsBeforeMonthEnd);
            var delays = Enumerable.Range(0, Backlog)
                .Select(index => ParseRawReportHandler.QuotaRetryDelay(TimeSpan.FromDays(1), now, index / (double)Backlog))
                .ToArray();
            delays.ShouldAllBe(delay => delay >= TimeSpan.FromSeconds(1) && delay <= TimeSpan.FromSeconds(ceiling));
            delays.Max().ShouldBe(TimeSpan.FromSeconds(ceiling));
            if (ceiling > 1)
            {
                delays.Distinct().Count().ShouldBe(Backlog);
            }
            else
            {
                delays.Distinct().ShouldHaveSingleItem().ShouldBe(TimeSpan.FromSeconds(1));
            }
        }
    }
}
