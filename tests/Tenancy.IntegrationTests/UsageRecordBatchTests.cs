// 12.3.1-T1.4: the ledger write path against a real PostgreSQL — the one-statement batch insert, the billing-key dedupe of
// ruling T43, the per-entry 422 of plan §5, the idempotency replay of ruling T11, the project check that keeps a foreign
// key from aborting the caller's transaction, the batch's one audit row, and the lock order that keeps two concurrent
// batches from deadlocking. Real pipeline, real transaction and real RLS, as in the sibling suites: the in-process half
// goes through the registered IUsageLedger inside a tenant scope, the operator half through the internal
// RecordUsageBatch command and the C2 pipeline.
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Usage.Application.Commands;
using Evidgate.Modules.Usage.Contracts;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 12.3.1-T1.4: <c>usage_ledger_entry</c>'s write path. AC1 is the dedupe — a resent metering event reports its billing key
/// and writes no second row; P2 is the one-statement budget — a batch of any size is one <c>INSERT … SELECT FROM
/// unnest(…)</c> plus one project check; the 422 rules name the offending entry by index; a replayed
/// <c>Idempotency-Key</c> answers the stored response without touching the ledger again; and the ledger writes one audit
/// row per batch that inserted, commits only with its caller and takes its unique-index locks in one order.
/// </summary>
/// <remarks>
/// The organization is never part of a request: every write reads it from the open tenant scope, which is why the
/// two-org case exists — the same billing key in another org is a different row, not a duplicate (ruling T43), and a
/// project of another org is invisible to the pre-check instead of being insertable.
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class UsageRecordBatchTests(PostgresTenancyFixture fixture, TenancyHostPool hosts)
    : IClassFixture<TenancyHostPool>
{
    private const string Period = "2026-10";

    /// <summary>
    /// The instant every entry carries unless a test says otherwise; read back to prove the round trip. It carries a
    /// microsecond part on purpose: with a whole-second instant a conversion that truncated to milliseconds would still
    /// pass, so the ledger's microsecond precision would go unproven.
    /// </summary>
    private static readonly DateTimeOffset OccurredAt =
        new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero).AddTicks(1_234_560);

    /// <summary>The same instant written from UTC+07:00: the ledger stores the instant, not the offset.</summary>
    private static readonly DateTimeOffset OccurredAtInPlusSeven = OccurredAt.ToOffset(TimeSpan.FromHours(7));

    private readonly PostgresTenancyFixture _fixture = fixture;

    // Reuse both host variants across this class's tenant-isolated shared-database tests.
    // World disposal returns each lease; the class fixture owns the hosts' final teardown.
    private readonly TenancyHostPool _hosts = hosts;

    /// <summary>
    /// One database and two hosts: <see cref="Api"/> resolves the tenant through the production filter, so the registry
    /// routes can create the org and its project, while <see cref="Commands"/> is the permissive host the write path is
    /// dispatched on — `billing:write` is staff-only and outside the service ceiling, so a real evaluator would refuse the
    /// dispatch for a reason this suite does not test.
    /// </summary>
    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        TenancyApiFactory Commands,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
            await Commands.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();

        var api = _hosts.RealResolver(database);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        // The write path's host: the evaluator is not what this suite tests.
        var commands = _hosts.GrantAll(database);

        return new World(api, client, commands, database, user, org, project);
    }

    private static UsageEntryInput Entry(
        string billingKey,
        Guid? project,
        decimal quantity = 1m,
        string period = Period,
        UsageMetric metric = UsageMetric.Results,
        string unit = "batch",
        DateTimeOffset? occurredAt = null) =>
        new(billingKey, metric, project, quantity, unit, period, occurredAt ?? OccurredAt);

    private static RecordUsageBatch Batch(string idempotencyKey, IReadOnlyList<UsageEntryInput> entries) =>
        new(entries, idempotencyKey);

    /// <summary>Records a batch through the registered <see cref="IUsageLedger"/> inside an explicit tenant scope.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entries">The entries to record.</param>
    /// <param name="org">The tenant to write as, or null for the world's own org.</param>
    /// <param name="cancellationToken">
    /// The test token; passed explicitly by the concurrency case, whose work runs on another flow.
    /// </param>
    private static async Task<UsageBatchResult> RecordAsync(
        World world,
        IReadOnlyList<UsageEntryInput> entries,
        Guid? org = null,
        CancellationToken cancellationToken = default)
    {
        var token = cancellationToken.CanBeCanceled
            ? cancellationToken
            : TestContext.Current.CancellationToken;

        await using var services = world.Commands.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(
            org ?? world.Org, null, false, token);
        var ledger = provider.GetRequiredService<IUsageLedger>();

        var result = await ledger.RecordAsync(
            new RecordUsageBatchRequest(entries), token);
        await scope.CommitAsync(token);

        return result;
    }

    /// <summary>Dispatches the operator-shaped command through the real Mediator pipeline.</summary>
    private static async Task<Result<UsageBatchResult>> SendAsync(World world, RecordUsageBatch command)
    {
        await using var services = world.Commands.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        using var tenant = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(world.Org, world.Project);
        using var principal = provider
            .GetRequiredService<IRequestPrincipalSetter>()
            .Publish(RequestPrincipal.Service(world.Project, [Permissions.Billing.Write]));

        return await provider.GetRequiredService<IMediator>().Send(command, TestContext.Current.CancellationToken);
    }

    private Task<long> LedgerCountAsync(World world, Guid org) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.usage_ledger_entry where org_id = '{org:D}'");

    /// <summary>How many audit rows one action of the world's org wrote.</summary>
    private Task<long> AuditCountAsync(World world, string action) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' and action = '{action}'");

    /// <summary>The org's audit rows of one action, as <c>action|object_type|object_id|count</c> in chain order.</summary>
    private Task<IReadOnlyList<string>> AuditRowsAsync(World world, string action) =>
        _fixture.ColumnAsync<string>(
            world.Database,
            $"""
            select action || '|' || object_type || '|' || object_id::text || '|' || coalesce(metadata ->> 'count', '')
            from public.audit_log
            where org_id = '{world.Org:D}' and action = '{action}'
            order by seq
            """);

    /// <summary>One column of the single ledger row of <paramref name="billingKey"/>.</summary>
    private async Task<string?> ColumnAsync(World world, Guid org, string billingKey, string column) =>
        await _fixture.ScalarAsync<string>(
            world.Database,
            $"select {column} from public.usage_ledger_entry where org_id = '{org:D}' and billing_key = '{billingKey}'");

    /// <summary>Every captured statement that mentions the ledger table, whichever way it touches it.</summary>
    private static IReadOnlyList<string> LedgerStatements(World world) =>
        [.. world.Commands.Statements.Snapshot().Where(statement => statement.Contains("public.usage_ledger_entry", StringComparison.Ordinal))];

    // ------------------------------------------------------------------ AC1: one row per billing key

    [Fact]
    public async Task RecordUsageBatch_DuplicateKey_IsIdempotent()
    {
        await using var world = await StartAsync();

        var first = await RecordAsync(
            world,
            [Entry("batch:1:results", world.Project), Entry("batch:2:results", world.Project, 2m)],
            cancellationToken: TestContext.Current.CancellationToken);

        first.Inserted.ShouldBe(["batch:1:results", "batch:2:results"]);
        first.Duplicates.ShouldBeEmpty();
        (await LedgerCountAsync(world, world.Org)).ShouldBe(2);

        // The same metering event arrives again: no second row, and the answer names the duplicate keys.
        var resent = await RecordAsync(
            world,
            [Entry("batch:1:results", world.Project), Entry("batch:2:results", world.Project, 2m)],
            cancellationToken: TestContext.Current.CancellationToken);

        resent.Inserted.ShouldBeEmpty();
        resent.Duplicates.ShouldBe(["batch:1:results", "batch:2:results"]);
        (await LedgerCountAsync(world, world.Org)).ShouldBe(2, "a resent metering event is one row, not two (AC1)");
        (await ColumnAsync(world, world.Org, "batch:1:results", "quantity::text")).ShouldBe("1");
        (await ColumnAsync(world, world.Org, "batch:2:results", "quantity::text")).ShouldBe("2");
    }

    [Fact]
    public async Task FindAsync_ReadsTheStoredRow_AndOtherOrgsSeeNothing()
    {
        await using var world = await StartAsync();
        const string BillingKey = "batch:find:results";

        (await RecordAsync(
            world,
            [Entry(BillingKey, world.Project, 3m)],
            cancellationToken: TestContext.Current.CancellationToken)).Inserted.ShouldBe([BillingKey]);

        var found = await FindAsync(world, BillingKey);

        found.ShouldNotBeNull();
        found.BillingKey.ShouldBe(BillingKey);
        found.Metric.ShouldBe(UsageLedgerMetricNames.Results);
        found.ProjectId.ShouldBe(world.Project);
        found.Quantity.ShouldBe(3m);
        found.Unit.ShouldBe("batch");
        found.Period.ShouldBe(Period);

        (await FindAsync(world, "batch:absent:results")).ShouldBeNull();

        // The key belongs to the org that recorded it: another org's read is an absence, never that row.
        var otherOrg = await CreateOrgAsync(world.Client, Guid.CreateVersion7(), Unique.Slug("other-find"));
        (await FindAsync(world, BillingKey, otherOrg)).ShouldBeNull();
    }

    /// <summary>One <see cref="IUsageLedger.FindAsync"/> call inside an explicit tenant scope.</summary>
    private static async Task<UsageEntryRecord?> FindAsync(World world, string billingKey, Guid? org = null)
    {
        var token = TestContext.Current.CancellationToken;

        await using var services = world.Commands.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(org ?? world.Org, null, false, token);

        return await provider.GetRequiredService<IUsageLedger>().FindAsync(billingKey, token);
    }

    [Fact]
    public async Task RecordUsageBatch_InBatchRepeatedKey_FirstOccurrenceWins()
    {
        await using var world = await StartAsync();

        // The repeat carries the same payload: a repeat with another payload is a conflict (see the payload-hash
        // suite), because one key cannot claim two bills.
        var result = await RecordAsync(
            world,
            [
                Entry("support:2026-10", null, 1m, metric: UsageMetric.SupportHours, unit: "hour"),
                Entry("support:2026-10", null, 1m, metric: UsageMetric.SupportHours, unit: "hour"),
                Entry("runner:2026-10", world.Project, 30m, metric: UsageMetric.RunnerMinutes, unit: "minute"),
                Entry(
                    "device:2026-10",
                    world.Project,
                    5m,
                    metric: UsageMetric.DeviceMinutes,
                    unit: "minute",
                    occurredAt: OccurredAtInPlusSeven),
            ],
            cancellationToken: TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(["support:2026-10", "runner:2026-10", "device:2026-10"]);
        result.Duplicates.ShouldBe(["support:2026-10"], "the repeat is reported, the first occurrence is the row");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(3);

        (await ColumnAsync(world, world.Org, "support:2026-10", "quantity::text")).ShouldBe("1");
        (await ColumnAsync(world, world.Org, "support:2026-10", "metric")).ShouldBe("support_hours");
        (await ColumnAsync(world, world.Org, "support:2026-10", "project_id::text")).ShouldBeNull();
        (await ColumnAsync(world, world.Org, "runner:2026-10", "quantity::text")).ShouldBe("30");

        // The unpriced columns stay null (DECISIONS O5, ruling T60): a row is never 0-priced.
        (await ColumnAsync(world, world.Org, "runner:2026-10", "price_ref::text")).ShouldBeNull();
        (await ColumnAsync(world, world.Org, "runner:2026-10", "amount::text")).ShouldBeNull();

        // `occurred_at` travels as a UTC timestamptz, so the round trip is exact — microsecond part included — and the
        // caller's offset never reaches the row.
        (await ColumnAsync(
            world,
            world.Org,
            "runner:2026-10",
            "to_char(occurred_at at time zone 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US')"))
            .ShouldBe("2026-10-12T09:30:00.123456");
        (await ColumnAsync(
            world,
            world.Org,
            "device:2026-10",
            "to_char(occurred_at at time zone 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US')"))
            .ShouldBe("2026-10-12T09:30:00.123456", "a UTC+07:00 entry stores the same instant");
    }

    [Fact]
    public async Task RecordUsageBatch_SameBillingKeyInTwoOrgs_BothInsert()
    {
        await using var world = await StartAsync();
        var otherOrg = await CreateOrgAsync(world.Client, world.User, Unique.Slug("globex"));

        var mine = await RecordAsync(
            world,
            [Entry("support:2026-10", null, 2m, metric: UsageMetric.SupportHours, unit: "hour")],
            cancellationToken: TestContext.Current.CancellationToken);
        var theirs = await RecordAsync(
            world,
            [Entry("support:2026-10", null, 3m, metric: UsageMetric.SupportHours, unit: "hour")],
            otherOrg,
            cancellationToken: TestContext.Current.CancellationToken);

        mine.Inserted.ShouldBe(["support:2026-10"]);
        theirs.Inserted.ShouldBe(["support:2026-10"], "the unique key is (org_id, billing_key), not the key alone (T43)");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(1);
        (await LedgerCountAsync(world, otherOrg)).ShouldBe(1);
        (await ColumnAsync(world, otherOrg, "support:2026-10", "quantity::text")).ShouldBe("3");
    }

    // ------------------------------------------------------------------ P2: one statement per batch

    [Fact]
    public async Task RecordUsageBatch_UsesOneStatement()
    {
        await using var world = await StartAsync();

        world.Commands.Statements.Clear();
        var result = await SendAsync(
            world,
            Batch(
                "usage-one-statement",
                [
                    Entry("batch:1:results", world.Project),
                    Entry("batch:2:results", world.Project),
                    Entry("batch:3:results", world.Project),
                ]));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value!.Inserted.Count.ShouldBe(3);

        var statements = LedgerStatements(world);
        statements.Count.ShouldBe(1, "the whole batch is one insert — no read-back and no per-row statement (T50, P2)");
        statements[0].ShouldStartWith("insert into public.usage_ledger_entry");
        statements[0].ShouldContain("on conflict (org_id, billing_key) do nothing");
        statements[0].ShouldContain("from unnest(");
        statements[0].ShouldContain("returning id, billing_key, period, original_period");

        // The project check is one read for the whole batch, not one per entry (review F-C), and it is what turns an
        // unknown project into a 422 instead of a 23503 that would abort the caller's transaction.
        world.Commands.Statements.Snapshot()
            .Count(statement => statement.Contains("from public.project", StringComparison.Ordinal))
            .ShouldBe(1);
    }

    // ------------------------------------------------------------------ occurred_at: exact at any epoch distance

    [Fact]
    public async Task RecordUsageBatch_Year2300_OccurredAtRoundTripsExactly()
    {
        await using var world = await StartAsync();
        var far = new DateTimeOffset(2300, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(1_234_560);

        var accepted = await RecordAsync(
            world,
            [Entry("batch:1:results", world.Project, occurredAt: far)],
            cancellationToken: TestContext.Current.CancellationToken);
        accepted.Inserted.ShouldBe(["batch:1:results"]);

        // Microseconds since the epoch for the year 2300 exceed 2^53, so a `double` microsecond count would round away
        // the fractional part; the instant travels as timestamptz instead (review F-E).
        (await ColumnAsync(
            world,
            world.Org,
            "batch:1:results",
            "to_char(occurred_at at time zone 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US')"))
            .ShouldBe("2300-12-31T23:59:59.123456");
    }

    // ------------------------------------------------------------------ audit: one row per batch that wrote rows

    [Fact]
    public async Task RecordUsageBatch_AuditRow_OnlyWhenTheBatchInserts()
    {
        await using var world = await StartAsync();

        var first = await SendAsync(
            world,
            Batch("usage-audit-1", [Entry("batch:1:results", world.Project), Entry("batch:2:results", world.Project)]));
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        first.Value!.Inserted.Count.ShouldBe(2);

        var rowId = await _fixture.ScalarAsync<string>(
            world.Database,
            $"select id::text from public.usage_ledger_entry where org_id = '{world.Org:D}' and billing_key = 'batch:1:results'");
        (await AuditRowsAsync(world, "usage.record_batch"))
            .ShouldBe(
                [$"usage.record_batch|usage_ledger|{rowId}|2"],
                "one row per recorded batch: its action, the first inserted ledger row and the count, nothing per entry");

        // A batch whose every key is already on file changed nothing, so it has no audit object.
        var duplicate = await SendAsync(world, Batch("usage-audit-2", [Entry("batch:1:results", world.Project)]));
        duplicate.IsSuccess.ShouldBeTrue(duplicate.Error?.Message);
        duplicate.Value!.Inserted.ShouldBeEmpty();
        (await AuditCountAsync(world, "usage.record_batch")).ShouldBe(1);

        // The same holds for an empty batch.
        var empty = await SendAsync(world, Batch("usage-audit-3", []));
        empty.IsSuccess.ShouldBeTrue(empty.Error?.Message);
        (await AuditCountAsync(world, "usage.record_batch")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ the ledger never commits alone

    [Fact]
    public async Task RecordAsync_WithoutCommit_LeavesNoRow()
    {
        await using var world = await StartAsync();
        await using var services = world.Commands.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        var token = TestContext.Current.CancellationToken;

        var scope = await scopes.BeginAsync(world.Org, null, false, token);
        await using (scope)
        {
            var ledger = provider.GetRequiredService<IUsageLedger>();
            var recorded = await ledger.RecordAsync(
                new RecordUsageBatchRequest([Entry("batch:1:results", world.Project)]), token);
            recorded.Inserted.ShouldBe(["batch:1:results"], "the row exists inside the caller's transaction");
        }

        (await LedgerCountAsync(world, world.Org))
            .ShouldBe(0, "the ledger commits with the caller's work and rolls back with it (00 §5.5 C3)");
    }

    // ------------------------------------------------------------------ F-A: concurrent batches, one lock order

    [Fact]
    public async Task RecordUsageBatch_ConcurrentSameNewKeys_NoDeadlock()
    {
        await using var world = await StartAsync();
        var token = TestContext.Current.CancellationToken;
        const int rounds = 6;

        // Each round inserts the same two *new* keys from two transactions in opposite request order. That is the shape
        // that deadlocks: with the keys not yet on file, each transaction's speculative insertion of one key waits on the
        // other's, so request order decides whether the two wait on each other (40P01) or on the same key first. The
        // store sorts the rows by billing key, so both transactions take the keys in one order and exactly one of them
        // inserts both while the other is told both are already on file. (A pre-committed key would prove nothing:
        // `ON CONFLICT DO NOTHING` takes no lock on a row that is already committed.)
        for (var round = 0; round < rounds; round++)
        {
            var a = $"order:{round}:a";
            var b = $"order:{round}:b";

            var forward = Task.Run(() => RecordAsync(
                world, [Entry(a, world.Project), Entry(b, world.Project)], cancellationToken: token));
            var backward = Task.Run(() => RecordAsync(
                world, [Entry(b, world.Project), Entry(a, world.Project)], cancellationToken: token));
            var results = await Task.WhenAll(forward, backward);

            // A deadlock would surface here as a PostgresException (40P01) from one of the two commits.
            ShouldRecordOrSkip(results[0], [a, b]);
            ShouldRecordOrSkip(results[1], [b, a]);
            (results[0].Inserted.Count + results[1].Inserted.Count)
                .ShouldBe(2, $"round {round}: the two keys are inserted once between the two batches");
        }

        (await LedgerCountAsync(world, world.Org))
            .ShouldBe(2 * rounds, "every round landed both keys once and no batch hit 40P01");
    }

    /// <summary>
    /// The answer of one of two racing batches over the same new keys: it either recorded both (and duplicates nothing)
    /// or found both already on file — never one of each, and always in its own request order.
    /// </summary>
    private static void ShouldRecordOrSkip(UsageBatchResult result, IReadOnlyList<string> keys)
    {
        if (result.Inserted.Count == 0)
        {
            result.Duplicates.ShouldBe(keys);
            return;
        }

        result.Inserted.ShouldBe(keys);
        result.Duplicates.ShouldBeEmpty();
    }
}
