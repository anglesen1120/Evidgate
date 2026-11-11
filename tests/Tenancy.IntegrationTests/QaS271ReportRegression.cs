using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Ingestion.Application;
using Evidgate.Modules.Ingestion.Application.Commands;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Usage.Application.Quota;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportQuotaTests
{
    [Trait("Category", "Docker")]
    public sealed class QaS271ReportRegression(PostgresTenancyFixture fixture, SeaweedFsStore store, TenancyHostPool hosts)
        : IClassFixture<SeaweedFsStore>, IClassFixture<TenancyHostPool>
    {
        private readonly EvidenceRawReportQuotaTests _subject = new(fixture, store, hosts);

        [Fact]
        public async Task Report_PeekAllowsThenCounterFills_DefersWithoutCommittingAndRecoversAfterRaise()
        {
            Func<Task>? fill = null;
            var gate = new ReportPreCheckGate(_ => fill?.Invoke() ?? Task.CompletedTask);
            var storage = new ReportStorageSpy();
            await using var world = await _subject.StartAsync(services =>
            {
                ReplaceQuotaGate(services, inner => gate.Wrap(inner));
                ReplaceObjectStorage(services, inner => storage.Wrap(inner));
            });
            await using var counters = await UsageQuotaWorld.StartAsync(fixture, hosts: hosts);
            await _subject.SeedPlanAsync(world);
            var artifact = await _subject.SeedArtifactAsync(world, OneEventBody);
            await _subject.SeedCounterAsync(counters, world, 0m, 1m);
            var task = await _subject.SeedDeferredTaskAsync(world, artifact);
            fill = () => fixture.ExecuteAsync(world.Database,
                $"update public.quota_counter set used = quota_limit where org_id = '{world.Org:D}'");

            var deferred = await Should.ThrowAsync<TaskDeferredException>(async () =>
                await CreateHandler(world).HandleAsync(new ParseRawReportPayload(world.RunId, artifact, ProducerKind), world.Org, Token));

            deferred.Delay.ShouldBeInRange(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(15));
            gate.Peeks.ShouldBe(1);
            gate.Checks.ShouldBe(1);
            storage.Gets.ShouldBe(1);
            (await _subject.CountAsync(world, "event")).ShouldBe(0);
            (await _subject.CountAsync(world, "event_batch")).ShouldBe(0);
            (await _subject.CountRawReportsAsync(world, artifact)).ShouldBe(0);
            (await _subject.CountOutboxAsync(world, UsageBatchAcceptedContract.TaskType)).ShouldBe(0);
            (await _subject.RunStateAsync(world)).ShouldBe("queued");
            var counter = (await _subject.ReadCounterAsync(counters, world)).ShouldNotBeNull();
            counter.Used.ShouldBe(1m);
            counter.BlockedAt.ShouldNotBeNull();
            (await fixture.ScalarAsync<long>(world.Database,
                $"select count(*) from public.task where id = '{task:D}'")).ShouldBe(1);

            fill = null;
            (await _subject.RaiseAsync(counters, world, world.Project, 2m)).IsSuccess.ShouldBeTrue();
            await CreateHandler(world).HandleAsync(new ParseRawReportPayload(world.RunId, artifact, ProducerKind), world.Org, Token);
            (await _subject.CountAsync(world, "event")).ShouldBe(1);
            (await _subject.CountRawReportsAsync(world, artifact)).ShouldBe(1);
            (await _subject.ReadCounterAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(2m);
        }

        [Fact]
        public async Task Report_ApplicationMonthDisagrees_GateBatchAndLedgerUseTheSameDatabaseMonth()
        {
            var spy = new QuotaGateSpy();
            var clock = new FakeTimeProvider(new DateTimeOffset(2020, 3, 31, 23, 59, 59, TimeSpan.Zero));
            await using var world = await _subject.StartAsync(services =>
            {
                UseClock(services, clock);
                ReplaceQuotaGate(services, inner => new SpyQuotaGate(inner, spy));
            });
            await _subject.SeedPlanAsync(world);
            await using var counters = await UsageQuotaWorld.StartAsync(fixture, hosts: hosts);

            // A counter with room, so the gate's answer is a metered one and names the transaction's month: an
            // unmetered Ok carries no month by contract, and the point here is which month the gate resolved.
            await _subject.SeedCounterAsync(counters, world, used: 0m, limit: 100m);
            var artifact = await _subject.SeedArtifactAsync(world, OneEventBody);
            (await ParseAsync(world, artifact)).Status.ShouldBe(ParseRawReportOutcome.Parsed);
            var request = spy.Requests.ShouldHaveSingleItem();
            var batch = await fixture.ScalarAsync<Guid>(world.Database,
                $"select id from public.event_batch where org_id = '{world.Org:D}'");
            var batchMonth = await fixture.ScalarAsync<string>(world.Database,
                $"select to_char(created_at at time zone 'UTC', 'YYYY-MM') from public.event_batch where id = '{batch:D}'");

            await using (var services = world.Host.Services.CreateAsyncScope())
            {
                var provider = services.ServiceProvider;
                await using var scope = await provider.GetRequiredService<ITenantScopeFactory>().BeginAsync(world.Org, null, false, Token);
                await new MeterEventBatchTaskHandler(
                        provider.GetRequiredService<IServiceScopeFactory>(),
                        NullLogger<MeterEventBatchTaskHandler>.Instance)
                    .HandleAsync(
                        new MeterEventBatchPayload(batch, IngestEventBatchHandler.UsageMetric, 1), scope, Token);
                await scope.CommitAsync(Token);
            }

            var ledgerMonth = await fixture.ScalarAsync<string>(world.Database,
                $"select period from public.usage_ledger_entry where org_id = '{world.Org:D}' and billing_key = 'batch:{batch:D}:event'");
            request.Period.ShouldBeNull("the gate resolves the month inside the batch's own transaction");
            var period = spy.Answers.ShouldHaveSingleItem().Period.ShouldNotBeNull();
            period.ShouldBe(batchMonth);
            period.ShouldBe(ledgerMonth);
            period.ShouldNotBe(clock.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture));
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public async Task Hosts_MismatchedGateFlags_OnlyTheEnabledPathRefuses(bool apiEnabled, bool workerEnabled)
        {
            await using var report = await _subject.StartAsync(services =>
                services.Configure<QuotaGateOptions>(options => options.Enabled = workerEnabled));
            await using var counters = await UsageQuotaWorld.StartAsync(fixture, hosts: hosts);
            await _subject.SeedPlanAsync(report);
            var artifact = await _subject.SeedArtifactAsync(report, OneEventBody);
            await _subject.SeedCounterAsync(counters, report, 1m, 1m);
            await _subject.SeedDeferredTaskAsync(report, artifact);

            await using var ingest = await IngestTestWorld.StartAsync(fixture, IngestTestWorld.Writer(), configuration:
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["EVIDGATE:Usage:QuotaGate:Enabled"] = apiEnabled.ToString(CultureInfo.InvariantCulture),
                });
            var month = await ingest.ScalarAsync<string>("select to_char(now() at time zone 'UTC', 'YYYY-MM')");
            await counters.SeedCounterAsync(new CounterSeed(Guid.CreateVersion7(), ingest.Project, month, "results", 1m, 1m, 0.8m, Org: ingest.Org));
            var body = IngestQaRequests.Batch([IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(ingest, 0), 1L,
                type: "test.step", status: null)]);
            var apiAnswer = await IngestQaRequests.PostEventsAsync(ingest.Client, ingest.RunId, body);
            var reportAnswer = await ParseAsync(report, artifact);

            apiAnswer.Status.ShouldBe(apiEnabled ? System.Net.HttpStatusCode.TooManyRequests : System.Net.HttpStatusCode.OK);
            reportAnswer.Status.ShouldBe(workerEnabled ? ParseRawReportOutcome.QuotaBlocked : ParseRawReportOutcome.Parsed);
            (await ingest.CountAsync("event_batch", $"org_id = '{ingest.Org:D}'")).ShouldBe(apiEnabled ? 0 : 1);
            (await _subject.CountAsync(report, "event_batch")).ShouldBe(workerEnabled ? 0 : 1);
            (await counters.CounterAsync(org: ingest.Org, project: ingest.Project, period: month)).ShouldNotBeNull().Used.ShouldBe(1m);
            (await _subject.ReadCounterAsync(counters, report)).ShouldNotBeNull().Used.ShouldBe(1m);
        }
    }
}
