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
            // A host clock at a month end the database is not in: were the month read from the application clock, the
            // gate would look for a counter of 2020-03 and admit the batch without charging the real one.
            var clock = new FakeTimeProvider(new DateTimeOffset(2020, 3, 31, 23, 59, 59, TimeSpan.Zero));
            var spy = new QuotaGateSpy();
            await using var world = await subject.StartIngestAsync(services: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                ReplaceWith<IQuotaGate>(services, inner => new SpyQuotaGate(inner, spy));
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

            clock.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture).ShouldBe("2020-03");
            request.Period.ShouldBeNull("the gate resolves the month inside the batch's own transaction");
            var period = spy.Answers.ShouldHaveSingleItem().Period.ShouldNotBeNull();
            period.ShouldBe(batchMonth);
            period.ShouldBe(ledgerMonth);
            period.ShouldNotBe("2020-03");
            world.Host.Statements.Snapshot().ShouldContain(statement =>
                statement.Contains("transaction_timestamp() at time zone 'UTC'", StringComparison.Ordinal));
        }
    }
}
