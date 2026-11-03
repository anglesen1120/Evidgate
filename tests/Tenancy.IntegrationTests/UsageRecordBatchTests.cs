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
public sealed class UsageRecordBatchTests
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

    private readonly PostgresTenancyFixture _fixture;

    public UsageRecordBatchTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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
        var database = await _fixture.CreateDatabaseAsync();

        var connectionString = _fixture.AppConnectionString(database);
        var api = TenancyApiFactory.RealResolver(connectionString);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        // The write path's host: the evaluator is not what this suite tests.
        var commands = TenancyApiFactory.GrantAll(connectionString);

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
    public async Task RecordUsageBatch_InBatchRepeatedKey_FirstOccurrenceWins()
    {
        await using var world = await StartAsync();

        var result = await RecordAsync(
            world,
            [
                Entry("support:2026-10", null, 1m, metric: UsageMetric.SupportHours, unit: "hour"),
                Entry("support:2026-10", null, 9m, metric: UsageMetric.SupportHours, unit: "hour"),
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
        var otherOrg = await CreateOrgAsync(world.Client, world.User, "globex");

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
        statements[0].ShouldContain("returning billing_key");

        // The project check is one read for the whole batch, not one per entry (review F-C), and it is what turns an
        // unknown project into a 422 instead of a 23503 that would abort the caller's transaction.
        world.Commands.Statements.Snapshot()
            .Count(statement => statement.Contains("from public.project", StringComparison.Ordinal))
            .ShouldBe(1);
    }

    // ------------------------------------------------------------------ 422: every entry rule names its index

    [Fact]
    public async Task RecordUsageBatch_InvalidEntries_422WithPerEntryIndices()
    {
        await using var world = await StartAsync();

        world.Commands.Statements.Clear();
        var refused = await SendAsync(
            world,
            Batch(
                "usage-invalid",
                [
                    Entry("batch:1:results", world.Project, metric: (UsageMetric)42),
                    Entry("batch:2:results", world.Project, period: "2026-13"),
                    Entry(" ", world.Project),
                    Entry(new string('k', IUsageLedger.MaxBillingKeyLength + 1), world.Project),
                    Entry("batch:5:results", world.Project, unit: " "),
                ]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        refused.Error.Code.ShouldBe("request_invalid");

        var details = refused.Error.Details!;
        details.Select(detail => (detail.Index, detail.Path, detail.Reason)).ShouldBe(
            [
                ((int?)0, "/entries/0/metric", "invalid_value"),
                (1, "/entries/1/period", "invalid_value"),
                (2, "/entries/2/billing_key", "required"),
                (3, "/entries/3/billing_key", "too_long"),
                (4, "/entries/4/unit", "required"),
            ],
            "one 00 §9.2 detail per offending entry, indexed by position");

        (await LedgerCountAsync(world, world.Org)).ShouldBe(0, "a refused batch writes nothing");
        LedgerStatements(world).ShouldBeEmpty("validation runs before the statement");
    }

    [Fact]
    public async Task RecordUsageBatch_NoEntriesMember_422OnTheBatch()
    {
        await using var world = await StartAsync();

        var refused = await SendAsync(world, new RecordUsageBatch(null!, "usage-null-entries"));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        var detail = refused.Error.Details!.ShouldHaveSingleItem();
        detail.Path.ShouldBe("/entries");
        detail.Reason.ShouldBe("required");
        detail.Index.ShouldBeNull("the missing member belongs to the batch, not to an entry");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);
    }

    [Fact]
    public async Task RecordUsageBatch_BatchCap_1000Accepted_1001Refused()
    {
        await using var world = await StartAsync();

        var overCap = Enumerable.Range(0, IUsageLedger.MaxBatchSize + 1)
            .Select(index => Entry($"batch:{index}:results", world.Project))
            .ToList();
        var refused = await SendAsync(world, Batch("usage-over-cap", overCap));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        var detail = refused.Error.Details!.ShouldHaveSingleItem();
        detail.Path.ShouldBe("/entries");
        detail.Reason.ShouldBe("too_many");
        detail.Index.ShouldBeNull();
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);

        var atCap = Enumerable.Range(0, IUsageLedger.MaxBatchSize)
            .Select(index => Entry($"batch:{index}:results", world.Project))
            .ToList();
        var accepted = await SendAsync(world, Batch("usage-at-cap", atCap));

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        accepted.Value!.Inserted.Count.ShouldBe(IUsageLedger.MaxBatchSize);
        accepted.Value.Duplicates.ShouldBeEmpty();
        (await LedgerCountAsync(world, world.Org)).ShouldBe(IUsageLedger.MaxBatchSize);
    }

    [Fact]
    public async Task RecordUsageBatch_BillingKeyLength_CountsCharactersNotUtf16Units()
    {
        await using var world = await StartAsync();

        // 200 emoji: 400 UTF-16 units but 200 characters — the unit the 1..200 column contract and PostgreSQL's
        // char_length use, and the reason the rule counts runes instead of `string.Length`.
        var atLimit = string.Concat(Enumerable.Repeat("\U0001F3AF", IUsageLedger.MaxBillingKeyLength));
        atLimit.Length.ShouldBe(IUsageLedger.MaxBillingKeyLength * 2, "the fixture key must be multibyte to prove the rule");

        var accepted = await RecordAsync(
            world,
            [Entry(atLimit, world.Project)],
            cancellationToken: TestContext.Current.CancellationToken);
        accepted.Inserted.ShouldBe([atLimit]);
        (await ColumnAsync(world, world.Org, atLimit, "char_length(billing_key)::text")).ShouldBe("200");

        var overLimit = atLimit + "\U0001F3AF";
        var refused = await SendAsync(world, Batch("usage-multibyte", [Entry(overLimit, world.Project)]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        var detail = refused.Error.Details!.ShouldHaveSingleItem();
        detail.Index.ShouldBe(0);
        detail.Path.ShouldBe("/entries/0/billing_key");
        detail.Reason.ShouldBe("too_long");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(1, "a refused batch writes nothing");
    }

    [Fact]
    public async Task RecordUsageBatch_EmptyBatch_IsANoOp()
    {
        await using var world = await StartAsync();

        world.Commands.Statements.Clear();
        var empty = await SendAsync(world, Batch("usage-empty", []));

        empty.IsSuccess.ShouldBeTrue(empty.Error?.Message);
        empty.Value!.Inserted.ShouldBeEmpty();
        empty.Value.Duplicates.ShouldBeEmpty();
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);
        LedgerStatements(world).ShouldBeEmpty("an empty batch writes nothing");

        // The in-process path answers the same shape without opening a statement either.
        var contractEmpty = await RecordAsync(
            world,
            [],
            cancellationToken: TestContext.Current.CancellationToken);
        contractEmpty.Inserted.ShouldBeEmpty();
        contractEmpty.Duplicates.ShouldBeEmpty();
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ T11: the idempotency record is the arbiter

    [Fact]
    public async Task RecordUsageBatch_IdempotencyReplay_ReturnsStoredResult_WithoutSecondInsert()
    {
        await using var world = await StartAsync();
        var command = Batch("replay-1", [Entry("batch:9:results", world.Project, 4m)]);

        var first = await SendAsync(world, command);
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        first.Value!.Inserted.ShouldBe(["batch:9:results"]);
        (await LedgerCountAsync(world, world.Org)).ShouldBe(1);

        var replay = await SendAsync(world, command);
        replay.IsSuccess.ShouldBeTrue(replay.Error?.Message);
        replay.Value!.Inserted.ShouldBe(first.Value.Inserted, "a replay answers the stored response verbatim");
        replay.Value.Duplicates.ShouldBe(first.Value.Duplicates);
        (await LedgerCountAsync(world, world.Org)).ShouldBe(1, "the replayed request inserted nothing");
        (await _fixture.ScalarAsync<long>(
            world.Database,
            "select count(*) from public.idempotency_record where operation = 'usage:record_batch'"))
            .ShouldBe(1);

        // The same key with a different payload is a conflict, and it writes nothing either.
        var conflict = await SendAsync(world, Batch("replay-1", [Entry("batch:9:results", world.Project, 5m)]));
        conflict.IsSuccess.ShouldBeFalse();
        conflict.Error!.Status.ShouldBe(409);
        conflict.Error.Code.ShouldBe("idempotency_conflict");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(1);
        (await ColumnAsync(world, world.Org, "batch:9:results", "quantity::text")).ShouldBe("4");
    }

    // ------------------------------------------------------------------ the in-process contract's own edge

    [Fact]
    public async Task RecordAsync_InvalidEntry_ThrowsBeforeAnyStatement()
    {
        await using var world = await StartAsync();

        world.Commands.Statements.Clear();
        var exception = await Should.ThrowAsync<ArgumentException>(
            async () => await RecordAsync(world, [Entry("batch:1:results", world.Project, period: "2026-13")]));

        exception.Message.ShouldContain("/entries/0/period");
        exception.Message.ShouldContain("invalid_value");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);
        LedgerStatements(world).ShouldBeEmpty("an in-process caller's bug never reaches the database");
    }

    // ------------------------------------------------------------------ projects: refused entry, never a 23503

    [Fact]
    public async Task RecordUsageBatch_UnknownProject_422AtItsEntry()
    {
        await using var world = await StartAsync();
        var unknown = Guid.CreateVersion7();

        world.Commands.Statements.Clear();
        var refused = await SendAsync(
            world,
            Batch(
                "usage-unknown-project",
                [Entry("batch:1:results", world.Project), Entry("batch:2:results", unknown)]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        var detail = refused.Error.Details!.ShouldHaveSingleItem();
        detail.Index.ShouldBe(1);
        detail.Path.ShouldBe("/entries/1/project_id");
        detail.Reason.ShouldBe("invalid_value");
        (await LedgerCountAsync(world, world.Org))
            .ShouldBe(0, "a refused batch writes nothing, not even the entries that were valid");
        LedgerStatements(world).ShouldBeEmpty("the project check runs before the insert");
    }

    [Fact]
    public async Task RecordUsageBatch_ForeignProject_RefusedAndCallerTransactionStaysUsable()
    {
        await using var world = await StartAsync();

        // Another organization's project, made by its own user: the id is real, and row security hides it from this org.
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "globex");
        var foreignProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "other");

        // The service path: the foreign project is invisible under RLS, so it is a refused entry, not a 23503 that the
        // pipeline would answer as a server error.
        var refused = await SendAsync(
            world,
            Batch("usage-foreign-project", [Entry("batch:1:results", foreignProject)]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        refused.Error.Details!.ShouldHaveSingleItem().Path.ShouldBe("/entries/0/project_id");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);

        // The in-process path: the refusal must leave the caller's transaction usable — it is mid-work and a 25P02
        // would break everything it still has to write.
        await using var services = world.Commands.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        var token = TestContext.Current.CancellationToken;
        await using var scope = await scopes.BeginAsync(world.Org, null, false, token);
        var ledger = provider.GetRequiredService<IUsageLedger>();

        var exception = await Should.ThrowAsync<ArgumentException>(
            async () => await ledger.RecordAsync(new RecordUsageBatchRequest([Entry("batch:1:results", foreignProject)]), token));
        exception.Message.ShouldContain("/entries/0/project_id");
        exception.Message.ShouldContain("invalid_value");

        var accepted = await ledger.RecordAsync(
            new RecordUsageBatchRequest([Entry("batch:2:results", world.Project)]), token);
        accepted.Inserted.ShouldBe(["batch:2:results"], "a refused entry leaves the transaction usable");
        await scope.CommitAsync(token);

        (await LedgerCountAsync(world, world.Org)).ShouldBe(1, "only the entry that named the caller's own project landed");
    }

    // ------------------------------------------------------------------ entry rules: quantity, unit, storable text

    [Fact]
    public async Task RecordUsageBatch_NegativeQuantity_422()
    {
        await using var world = await StartAsync();

        var refused = await SendAsync(
            world,
            Batch("usage-negative", [Entry("batch:1:results", world.Project, -1m)]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        var detail = refused.Error.Details!.ShouldHaveSingleItem();
        detail.Index.ShouldBe(0);
        detail.Path.ShouldBe("/entries/0/quantity");
        detail.Reason.ShouldBe("invalid_value");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);

        // The in-process path applies the same rule; a correction is an adjustment row, never a negative plain row.
        var exception = await Should.ThrowAsync<ArgumentException>(
            async () => await RecordAsync(world, [Entry("batch:1:results", world.Project, -1m)]));
        exception.Message.ShouldContain("/entries/0/quantity");
        exception.Message.ShouldContain("invalid_value");
    }

    [Fact]
    public async Task RecordUsageBatch_UnstorableText_422()
    {
        await using var world = await StartAsync();

        var refused = await SendAsync(
            world,
            Batch(
                "usage-unstorable",
                [
                    Entry("batch:\0:results", world.Project),                      // NUL in the billing key
                    Entry("batch:2:results", world.Project, unit: "batch\0"),      // NUL in the unit
                    Entry("batch:\uD800:results", world.Project),                  // lone high surrogate in the key
                    Entry("batch:4:results", world.Project, unit: "\uDC00"),       // lone low surrogate in the unit
                    Entry("batch:\U0001F3AF:results", world.Project),              // a real pair is storable
                ]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        refused.Error.Details!.Select(detail => (detail.Index, detail.Path, detail.Reason)).ShouldBe(
            [
                ((int?)0, "/entries/0/billing_key", "invalid_value"),
                (1, "/entries/1/unit", "invalid_value"),
                (2, "/entries/2/billing_key", "invalid_value"),
                (3, "/entries/3/unit", "invalid_value"),
            ],
            "U+0000 and a lone surrogate cannot be stored as text, so they are refused before the statement");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);

        var accepted = await SendAsync(
            world,
            Batch("usage-astral", [Entry("batch:\U0001F3AF:results", world.Project)]));
        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        accepted.Value!.Inserted.ShouldBe(["batch:\U0001F3AF:results"], "a surrogate pair is one storable character");
    }

    [Fact]
    public async Task RecordUsageBatch_UnitLength_CountsCharactersAndCapsAt32()
    {
        await using var world = await StartAsync();

        var atLimit = string.Concat(Enumerable.Repeat("\U0001F3AF", IUsageLedger.MaxUnitLength));
        atLimit.Length.ShouldBe(IUsageLedger.MaxUnitLength * 2, "the fixture unit must be multibyte to prove the rule");

        var accepted = await RecordAsync(
            world,
            [Entry("batch:1:results", world.Project, unit: atLimit)],
            cancellationToken: TestContext.Current.CancellationToken);
        accepted.Inserted.ShouldBe(["batch:1:results"]);
        (await ColumnAsync(world, world.Org, "batch:1:results", "char_length(unit)::text")).ShouldBe("32");

        var overLimit = atLimit + "\U0001F3AF";
        var refused = await SendAsync(
            world,
            Batch("usage-unit-over", [Entry("batch:2:results", world.Project, unit: overLimit)]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        var detail = refused.Error.Details!.ShouldHaveSingleItem();
        detail.Index.ShouldBe(0);
        detail.Path.ShouldBe("/entries/0/unit");
        detail.Reason.ShouldBe("too_long");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(1);
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
