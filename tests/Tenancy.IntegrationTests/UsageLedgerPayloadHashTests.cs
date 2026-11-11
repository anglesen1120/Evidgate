// CR-21: the ledger's payload hash on a real tenant. A resend of the same billable payload stays a verified replay (one
// row, a duplicate tally, nothing billed twice), while a reused billing key claiming another payload is a conflict that
// writes nothing at all — not the conflicting row, not the new key beside it, not a duplicate tally. Real pipeline, real
// transaction and real RLS, as in the sibling suites: the in-process half goes through the registered IUsageLedger
// inside a tenant scope.
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// CR-21: what a billing key claims. The ledger stores the canonical hash of the billable payload (<c>payload_hash</c>),
/// so a key already on file with the same hash is idempotent and a key already on file with another hash is a conflict
/// that rolls the whole batch back. <c>occurred_at</c> is not part of the claim, and the key itself is the identity.
/// </summary>
/// <remarks>
/// The world is the sibling ledger suite's: one shared database, a real-resolver host to create the org and its project,
/// and a permissive host to dispatch the write path (the evaluator is not what this suite tests). Library meters stay
/// off through <c>LeanLibraryMetrics</c>; the one Evidgate counter a case asserts is captured by a listener the case
/// installs for its own duration.
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class UsageLedgerPayloadHashTests
{
    private const string Period = "2026-10";

    /// <summary>A distinct instant, so a hash that leaked <c>occurred_at</c> into its input would change.</summary>
    private static readonly DateTimeOffset OccurredAt =
        new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero).AddTicks(1_234_560);

    private readonly PostgresTenancyFixture _fixture;

    public UsageLedgerPayloadHashTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>One database and two hosts, with a log sink on the write path's host.</summary>
    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        TenancyApiFactory Commands,
        ConflictLogSink Log,
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

        var connectionString = _fixture.AppConnectionString(database);
        var api = TenancyApiFactory.RealResolver(connectionString);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        // The write path's host, with a sink so a conflict's log line and level can be read back.
        var log = new ConflictLogSink();
        var commands = TenancyApiFactory.GrantAll(
            connectionString,
            services => services.AddSingleton<ILoggerProvider>(log));

        return new World(api, client, commands, log, database, user, org, project);
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

    /// <summary>Records a batch through the registered <see cref="IUsageLedger"/> inside an explicit tenant scope.</summary>
    private static async Task<UsageBatchResult> RecordAsync(
        World world,
        IReadOnlyList<UsageEntryInput> entries)
    {
        var token = TestContext.Current.CancellationToken;

        await using var services = world.Commands.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, token);

        var result = await provider.GetRequiredService<IUsageLedger>()
            .RecordAsync(new RecordUsageBatchRequest(entries), token);
        await scope.CommitAsync(token);

        return result;
    }

    private Task<long> LedgerCountAsync(World world) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.usage_ledger_entry where org_id = '{world.Org:D}'");

    private Task<long> DuplicateAttemptsAsync(World world) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.usage_duplicate_attempt where org_id = '{world.Org:D}'");

    private Task<long> RollupCountAsync(World world) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.usage_rollup where org_id = '{world.Org:D}'");

    /// <summary>One column of the single ledger row of <paramref name="billingKey"/>.</summary>
    private Task<string> ColumnAsync(World world, string billingKey, string column) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            $"select {column} from public.usage_ledger_entry where org_id = '{world.Org:D}' "
            + $"and billing_key = '{billingKey}'");

    /// <summary>The hash the ledger must store for <paramref name="entry"/>: the same canonical digest the batch computes.</summary>
    private static string HashOf(UsageEntryInput entry) =>
        UsagePayloadHash.Compute(
            UsageMetricWire.ToWireValue(entry.Metric),
            entry.ProjectId,
            entry.Quantity,
            entry.Unit,
            entry.Period);

    // ------------------------------------------------------------------ a verified replay

    [Fact]
    public async Task Replay_IdenticalPayload_OneRow_SecondIsADuplicate()
    {
        await using var world = await StartAsync();
        var key = Unique.Slug("replay");
        var entry = Entry(key, world.Project, quantity: 1.5m);

        var first = await RecordAsync(world, [entry]);
        first.Inserted.ShouldBe([key]);
        first.Duplicates.ShouldBeEmpty();
        first.Conflicts.ShouldBeEmpty();
        (await ColumnAsync(world, key, "payload_hash")).ShouldBe(HashOf(entry));

        var resent = await RecordAsync(world, [entry]);

        resent.Inserted.ShouldBeEmpty();
        resent.Duplicates.ShouldBe([key]);
        resent.Conflicts.ShouldBeEmpty();
        (await LedgerCountAsync(world)).ShouldBe(1, "a verified replay is one row");
        (await ColumnAsync(world, key, "quantity::text")).ShouldBe("1.5");
        (await DuplicateAttemptsAsync(world)).ShouldBe(1, "a verified replay is still tallied as a duplicate attempt");
    }

    /// <summary>
    /// <c>occurred_at</c> is deliberately not hashed: a meter re-deriving the batch's instant from another clock still
    /// bills the same fact, so the replay must land as a duplicate.
    /// </summary>
    [Fact]
    public async Task Replay_OccurredAtOnlyDiffers_IsADuplicate()
    {
        await using var world = await StartAsync();
        var key = Unique.Slug("instant");
        var entry = Entry(key, world.Project);

        (await RecordAsync(world, [entry])).Inserted.ShouldBe([key]);

        var later = await RecordAsync(world, [entry with { OccurredAt = entry.OccurredAt.AddHours(3) }]);

        later.Duplicates.ShouldBe([key]);
        later.Conflicts.ShouldBeEmpty();
        (await LedgerCountAsync(world)).ShouldBe(1);
        (await DuplicateAttemptsAsync(world)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ a conflict, per hashed field

    /// <summary>
    /// Every hashed field is billable identity: changing one of them under an existing key is a conflict, whatever the
    /// other four still say.
    /// </summary>
    /// <param name="field">The field the second request changes.</param>
    [Theory]
    [InlineData("metric")]
    [InlineData("project_id")]
    [InlineData("quantity")]
    [InlineData("unit")]
    [InlineData("period")]
    public async Task Replay_AnyHashedFieldChanged_IsAConflict(string field)
    {
        await using var world = await StartAsync();
        var key = Unique.Slug("field");
        var entry = Entry(key, world.Project, quantity: 1.5m);

        (await RecordAsync(world, [entry])).Inserted.ShouldBe([key]);

        var changed = field switch
        {
            "metric" => entry with { Metric = UsageMetric.ArtifactGbMonth },
            "project_id" => entry with { ProjectId = null },
            "quantity" => entry with { Quantity = 2.5m },
            "unit" => entry with { Unit = "furlong" },
            "period" => entry with { Period = "2026-11" },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "unknown hashed field"),
        };

        var attempt = await RecordAsync(world, [changed]);

        attempt.Conflicts.ShouldBe([key], "the key claims another payload");
        attempt.Inserted.ShouldBeEmpty();
        attempt.Duplicates.ShouldBeEmpty();

        (await LedgerCountAsync(world)).ShouldBe(1);
        (await ColumnAsync(world, key, "quantity::text")).ShouldBe("1.5", "the first write of a key is never rewritten");
        (await ColumnAsync(world, key, "metric")).ShouldBe(UsageLedgerMetricNames.Results);
        (await DuplicateAttemptsAsync(world)).ShouldBe(0, "a conflict is not a duplicate attempt");
        (await RollupCountAsync(world)).ShouldBe(0, "nothing was billed, so nothing is rolled up");
    }

    // ------------------------------------------------------------------ the batch is atomic

    /// <summary>
    /// One conflicting line fails the whole batch: the new key beside it is not written either, because the store rolls
    /// back to its savepoint before it answers.
    /// </summary>
    [Fact]
    public async Task Batch_NewKeyBesideAConflictingKey_WritesNothing()
    {
        await using var world = await StartAsync();
        var existing = Unique.Slug("atomic");
        var fresh = Unique.Slug("atomic-new");

        (await RecordAsync(world, [Entry(existing, world.Project)])).Inserted.ShouldBe([existing]);

        var result = await RecordAsync(
            world,
            [
                Entry(fresh, world.Project),
                Entry(existing, world.Project, quantity: 9m),
            ]);

        result.Conflicts.ShouldBe([existing]);
        result.Inserted.ShouldBeEmpty();
        result.Duplicates.ShouldBeEmpty();
        (await LedgerCountAsync(world)).ShouldBe(1, "the non-conflicting key of the same batch is rolled back too");
        (await ColumnAsync(world, existing, "quantity::text")).ShouldBe("1");
        (await DuplicateAttemptsAsync(world)).ShouldBe(0);
        (await ColumnAsync(world, fresh, "id::text")).ShouldBeNull();
    }

    // ------------------------------------------------------------------ in-request repeats

    [Fact]
    public async Task InRequestRepeat_DifferentPayload_IsAConflict()
    {
        await using var world = await StartAsync();
        var key = Unique.Slug("in-request");

        var result = await RecordAsync(
            world,
            [
                Entry(key, world.Project, quantity: 1m),
                Entry(key, world.Project, quantity: 2m),
            ]);

        result.Conflicts.ShouldBe([key], "one key cannot claim two payloads, even inside one request");
        result.Inserted.ShouldBeEmpty();
        result.Duplicates.ShouldBeEmpty();
        (await LedgerCountAsync(world)).ShouldBe(0, "a key that conflicts with itself writes nothing");
        (await DuplicateAttemptsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task InRequestRepeat_IdenticalPayload_IsStillADuplicate()
    {
        await using var world = await StartAsync();
        var key = Unique.Slug("in-request-same");

        var result = await RecordAsync(
            world,
            [
                Entry(key, world.Project, quantity: 1.50m),
                Entry(key, world.Project, quantity: 1.5m, occurredAt: OccurredAt.AddMinutes(1)),
            ]);

        result.Inserted.ShouldBe([key]);
        result.Duplicates.ShouldBe([key], "the repeat is reported, the first occurrence is the row");
        result.Conflicts.ShouldBeEmpty();
        (await LedgerCountAsync(world)).ShouldBe(1);
        (await DuplicateAttemptsAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// An all-duplicate replay is a silent success: no conflict, and the conflict counter (listened for on the Evidgate
    /// meter) is never incremented.
    /// </summary>
    [Fact]
    public async Task Replay_AllDuplicates_IsASilentSuccess()
    {
        await using var world = await StartAsync();
        using var listener = ConflictCounter(out var measured);

        var key = Unique.Slug("silent");
        var entry = Entry(key, world.Project, quantity: 1.5m);
        (await RecordAsync(world, [entry])).Inserted.ShouldBe([key]);

        // A resent batch and an in-request repeat of the same payload: both are verified replays, never conflicts.
        var replay = await RecordAsync(world, [entry, entry]);

        replay.Conflicts.ShouldBeEmpty();
        replay.Inserted.ShouldBeEmpty("the key is already on file, so this batch inserts nothing");
        replay.Duplicates.ShouldBe([key], "an in-request repeat is still a duplicate, not a conflict");
        measured.ShouldBeEmpty("an idempotent replay is not a conflict");
    }

    /// <summary>
    /// The billing effect of a verified replay: the usage-units counter (what a roll-up and a statement derive from) is
    /// measured once for the bill, never once per attempt — and a conflict measures nothing at all.
    /// </summary>
    [Fact]
    public async Task Replay_AndConflict_BillTheAcceptedPayloadOnce()
    {
        await using var world = await StartAsync();
        using var units = IngestTelemetryCapture.UsageUnits(out var measured);

        var key = Unique.Slug("charged-once");
        var entry = Entry(key, world.Project, quantity: 2.5m);

        (await RecordAsync(world, [entry])).Inserted.ShouldBe([key]);
        (await RecordAsync(world, [entry])).Duplicates.ShouldBe([key], "a verified replay is not billed again");
        (await RecordAsync(world, [entry with { Quantity = 9m }])).Conflicts.ShouldBe([key]);

        var billed = measured.ToArray();

        billed.Length.ShouldBe(1, "one measurement for the accepted payload, none for the replay or the conflict");
        billed[0].Metric.ShouldBe(UsageLedgerMetricNames.Results);
        billed[0].Quantity.ShouldBe(2.5);
        (await RollupCountAsync(world)).ShouldBe(0, "a conflict changes no derived total");
    }

    /// <summary>
    /// Listens for the Evidgate conflicts counter and keeps one entry per measurement. The listener is narrow on purpose:
    /// the counter is the only instrument this suite reads, and no label is asserted because the catalogue is its own test.
    /// </summary>
    /// <param name="measurements">Every value measured while the listener runs.</param>
    private static MeterListener ConflictCounter(out ConcurrentQueue<long> measurements)
    {
        var values = new ConcurrentQueue<long>();
        measurements = values;

        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == EvidgateTelemetry.MeterName
                    && instrument.Name == EvidgateTelemetry.UsageBillingKeyConflictMetricName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => values.Enqueue(value));
        listener.Start();

        return listener;
    }
}
