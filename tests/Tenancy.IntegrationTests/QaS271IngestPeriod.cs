using System.Globalization;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class IngestQuotaTests
{
    [Trait("Category", "Docker")]
    [Collection(ExclusiveTenancySuite.Name)]
    public sealed class QaS271IngestPeriod(PostgresTenancyFixture fixture)
    {
        [Fact]
        public async Task Ingest_ApplicationCrossesMonthEnd_GateBatchAndLedgerKeepTheDatabaseTransactionMonth()
        {
            var subject = new IngestQuotaTests(fixture);
            var clock = new FakeTimeProvider(new DateTimeOffset(2020, 3, 31, 23, 59, 59, TimeSpan.Zero));
            var spy = new QuotaGateSpy();
            await using var world = await subject.StartIngestAsync(services: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                ReplaceWith<IQuotaGate>(services, inner => new CrossingClockGate(new SpyQuotaGate(inner, spy), clock));
            });
            var answer = await PostBatchAsync(world, (Guid.CreateVersion7(), 1L));
            answer.Status.ShouldBe(System.Net.HttpStatusCode.OK, answer.ErrorMessage);
            var batch = answer.Body.GetProperty("batch_id").GetGuid();
            var request = spy.Requests.ShouldHaveSingleItem();
            var batchMonth = await world.ScalarAsync<string>(
                $"select to_char(created_at at time zone 'UTC', 'YYYY-MM') from public.event_batch where id = '{batch:D}'");
            await MeterAsync(world, batch, 1);
            var ledgerMonth = await world.ScalarAsync<string>(
                $"select period from public.usage_ledger_entry where org_id = '{world.Org:D}' and billing_key = 'batch:{batch:D}:event'");

            clock.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture).ShouldBe("2020-04");
            request.Period.ShouldBe(batchMonth);
            request.Period.ShouldBe(ledgerMonth);
            request.Period.ShouldNotBe("2020-03");
            request.Period.ShouldNotBe("2020-04");
            world.Host.Statements.Snapshot().ShouldContain(statement =>
                statement.Contains("transaction_timestamp() at time zone 'UTC'", StringComparison.Ordinal));
        }

        private sealed class CrossingClockGate(IQuotaGate inner, FakeTimeProvider clock) : IQuotaGate
        {
            public Task<QuotaCheckResult> CheckAsync(QuotaCheckRequest request, CancellationToken cancellationToken) =>
                inner.CheckAsync(request, cancellationToken);

            public async Task<string> CurrentPeriodAsync(CancellationToken cancellationToken)
            {
                var period = await inner.CurrentPeriodAsync(cancellationToken);
                clock.Advance(TimeSpan.FromSeconds(2));
                return period;
            }

            public Task<QuotaCheckResult> PeekAsync(QuotaCheckRequest request, CancellationToken cancellationToken) =>
                inner.PeekAsync(request, cancellationToken);
        }
    }
}
