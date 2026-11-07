// The advisory quota read and the transaction month: a peek blocks only on a counter of the period that has no room,
// writes and carries nothing, and the month callers gate with is the month the caller's own rows are stamped with.
using Evidgate.Modules.Usage.Application.Quota;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The read-only half of the gate against a real PostgreSQL: a period whose counter has no room answers Block with the
/// wait and the refusing scope, a period with room or with no counter at all answers Ok, and neither answer writes a
/// counter, stamps one or carries a limit forward.
/// </summary>
[Trait("Category", "Docker")]
public sealed class UsageQuotaPeekTests
{
    private const string Period = "2026-10";
    private const string PreviousPeriod = "2026-09";
    private const decimal Limit = 10m;
    private const decimal WarnRatio = 0.8m;

    private readonly PostgresTenancyFixture _fixture;

    public UsageQuotaPeekTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static QuotaCheckRequest Request(
        UsageQuotaWorld world,
        decimal quantity,
        string period = Period,
        Guid? project = null,
        bool includeTenantScope = false) =>
        new(UsageMetric.Results, period, project ?? world.Project, quantity, includeTenantScope);

    private static Task<Guid> SeedAsync(
        UsageQuotaWorld world,
        decimal used,
        decimal limit = Limit,
        string period = Period,
        Guid? projectId = null,
        Guid? org = null,
        DateTimeOffset? blockedAt = null) =>
        world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(),
            projectId,
            period,
            "results",
            used,
            limit,
            WarnRatio,
            null,
            blockedAt,
            org));

    // ------------------------------------------------------------------ the refusal

    [Fact]
    public async Task QuotaGatePeek_ProjectCounterFull_Blocks()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: Limit, projectId: world.Project);

        var blocked = await world.PeekAsync(Request(world, 1m));

        blocked.Decision.ShouldBe(QuotaDecision.Block);
        blocked.Used.ShouldBe(Limit);
        blocked.Limit.ShouldBe(Limit);
        blocked.RetryAfter.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        blocked.RaisePath.ShouldBe($"/internal/operator/billing/orgs/{world.Org:D}/quota-raise");

        // The project's own counter is the one that refused: the tenant scope was not named.
        blocked.RefusedScope.ShouldBe(QuotaScope.Project, "the project's counter of the period has no room");
        (await world.CounterCountAsync()).ShouldBe(1, "a peek creates no counter");
    }

    [Fact]
    public async Task QuotaGatePeek_TenantCounterFull_BlocksTenantScope()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: Limit, projectId: null);
        await SeedAsync(world, used: 0m, projectId: world.Project);

        var bothScopes = await world.PeekAsync(Request(world, 1m, includeTenantScope: true));

        bothScopes.Decision.ShouldBe(QuotaDecision.Block);
        bothScopes.RefusedScope.ShouldBe(QuotaScope.Tenant, "the organization-wide counter is the first that refuses");

        // The positive control: the project's own counter has room, so the same call without the tenant scope proceeds.
        var projectOnly = await world.PeekAsync(Request(world, 1m));

        projectOnly.Decision.ShouldBe(QuotaDecision.Ok);
    }

    [Fact]
    public async Task QuotaGatePeek_ForeignOrgFullCounters_DoNotBlock()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await SeedAsync(world, used: Limit, projectId: otherProject, org: otherOrg);
        await SeedAsync(world, used: Limit, projectId: null, org: otherOrg);
        await SeedAsync(world, used: 0m, projectId: world.Project);
        var foreignProject = await world.CounterAsync(org: otherOrg, project: otherProject);
        var foreignTenant = await world.CounterAsync(org: otherOrg);

        var projectOnly = await world.PeekAsync(Request(world, 1m));

        projectOnly.Decision.ShouldBe(QuotaDecision.Ok, "another organization's full counter refuses nothing here");
        var bothScopes = await world.PeekAsync(Request(world, 1m, includeTenantScope: true));

        bothScopes.Decision.ShouldBe(
            QuotaDecision.Ok,
            "the same period, metric and tenant scope under another organization is a different counter");

        // The positive control: the same two counters under this organization refuse the same question.
        await SeedAsync(world, used: Limit, projectId: null);
        var refused = await world.PeekAsync(Request(world, 1m, includeTenantScope: true));

        refused.Decision.ShouldBe(QuotaDecision.Block);
        refused.RefusedScope.ShouldBe(QuotaScope.Tenant, "this organization's own full tenant counter refuses");
        (await world.CounterAsync(org: otherOrg, project: otherProject)).ShouldBe(foreignProject);
        (await world.CounterAsync(org: otherOrg)).ShouldBe(foreignTenant);
    }

    // ------------------------------------------------------------------ the admission

    [Fact]
    public async Task QuotaGatePeek_RoomOrExactFit_ReturnsOk()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 9m, projectId: world.Project);

        var exactFit = await world.PeekAsync(Request(world, 1m));
        exactFit.Decision.ShouldBe(QuotaDecision.Ok, "the last unit inside the limit still fits");

        var overTheLine = await world.PeekAsync(Request(world, 2m));
        overTheLine.Decision.ShouldBe(QuotaDecision.Block, "the same counter has no room for two units");
    }

    [Fact]
    public async Task QuotaGatePeek_NoCounterOfThePeriod_ReturnsOkWithoutCarry()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: Limit, period: PreviousPeriod, projectId: world.Project);

        var answer = await world.PeekAsync(Request(world, 1m));

        answer.Decision.ShouldBe(QuotaDecision.Ok, "a period without a counter has no configured limit to refuse with");
        (await world.CounterCountAsync()).ShouldBe(1, "the earlier month's limit is not carried into this period");
        (await world.CounterIdAsync(project: world.Project, period: Period)).ShouldBeNull();
        (await world.CounterAsync(project: world.Project, period: PreviousPeriod))
            .ShouldNotBeNull().Used.ShouldBe(Limit, "the earlier month's counter is not written either");
    }

    // ------------------------------------------------------------------ it reads, it never writes

    [Fact]
    public async Task QuotaGatePeek_Block_LeavesTheCounterUntouched()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 5m, projectId: world.Project);
        var before = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        world.ClearStatementLog();

        var blocked = await world.PeekAsync(Request(world, 6m));

        blocked.Decision.ShouldBe(QuotaDecision.Block);
        (await world.CounterAsync(project: world.Project)).ShouldBe(
            before,
            "a peek reserves nothing, stamps nothing and bumps no version");

        world.CounterStatements().Count.ShouldBe(1, "a peek is one read of the counter table, never a read and a write");
        world.CounterStatements().ShouldNotContain(
            statement => statement.Contains("insert", StringComparison.OrdinalIgnoreCase)
                || statement.Contains("update", StringComparison.OrdinalIgnoreCase)
                || statement.Contains("for update", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task QuotaGatePeek_InvalidRequest_RefusedBeforeAnyStatement()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m, projectId: world.Project);
        world.ClearStatementLog();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => world.PeekAsync(Request(world, 1m, period: "2026-13")));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => world.PeekAsync(Request(world, 0m)));

        world.CounterStatements().ShouldBeEmpty("a caller bug is refused before any statement runs");
    }

    [Fact]
    public async Task QuotaGatePeek_Disabled_ReturnsOkWithoutRead()
    {
        await using var world = await UsageQuotaWorld.StartAsync(
            _fixture,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Usage:QuotaGate:Enabled"] = "false",
            });
        await SeedAsync(world, used: Limit, projectId: world.Project, blockedAt: DateTimeOffset.UnixEpoch);
        var before = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        world.ClearStatementLog();

        var answer = await world.PeekAsync(Request(world, 1m));

        answer.Decision.ShouldBe(QuotaDecision.Ok, "the operator switch answers Ok explicitly, it is not a stub");
        answer.RetryAfter.ShouldBeNull();
        world.CounterStatements().ShouldBeEmpty("a switched-off gate does not read a counter");

        var after = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        after.Used.ShouldBe(before.Used);
        after.Version.ShouldBe(before.Version);
        after.BlockedAt.ShouldBe(before.BlockedAt);
    }

    // ------------------------------------------------------------------ the transaction's billing month

    [Fact]
    public async Task QuotaGateCurrentPeriod_IsTheTransactionAndRowMonth()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);

        var (gate, transaction, stamped) = await world.PeriodAsync();

        gate.ShouldBe(transaction, "the gate answers the month of the caller's open transaction");
        gate.ShouldBe(stamped, "the ledger row metered in that transaction carries the same month");
        gate.Length.ShouldBe(7);
        char.IsAsciiDigit(gate[0]).ShouldBeTrue();
        gate[4].ShouldBe('-');
    }

    [Fact]
    public async Task QuotaGateCurrentPeriod_DisabledSwitch_StillAnswers()
    {
        await using var world = await UsageQuotaWorld.StartAsync(
            _fixture,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Usage:QuotaGate:Enabled"] = "false",
            });

        var (gate, transaction, stamped) = await world.PeriodAsync();

        gate.ShouldBe(transaction, "the period is not gated: callers meter their rows with it either way");
        gate.ShouldBe(stamped);
    }
}

/// <summary>
/// The boot-time announcement of the gate switch: a host that switched the gate off says so at Warning and names the
/// switch the other host must match; a host that left it on says it once at Information.
/// </summary>
public sealed class UsageQuotaStartupNoticeTests
{
    [Fact]
    public async Task QuotaGateStartupNotice_Disabled_LogsTheWarningOnly()
    {
        var logger = new NoticeLogger();
        var notice = new QuotaGateStartupNotice(Options.Create(new QuotaGateOptions { Enabled = false }), logger);

        await notice.StartAsync(TestContext.Current.CancellationToken);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.EventId.ShouldBe(2473);
        entry.Message.ShouldContain("disabled", Case.Insensitive);
        entry.Message.ShouldContain("QuotaGate Enabled=false");
        entry.Message.ShouldContain("Api");
        entry.Message.ShouldContain("Worker");
    }

    [Fact]
    public async Task QuotaGateStartupNotice_Enabled_LogsInformationAndNoWarning()
    {
        var logger = new NoticeLogger();
        var notice = new QuotaGateStartupNotice(Options.Create(new QuotaGateOptions { Enabled = true }), logger);

        await notice.StartAsync(TestContext.Current.CancellationToken);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.EventId.ShouldBe(2472);
        logger.Entries.ShouldNotContain(line => line.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task QuotaGateStartupNotice_ResolvesThroughTheContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<QuotaGateOptions>(options => options.Enabled = true);
        services.AddHostedService<QuotaGateStartupNotice>();

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ShouldHaveSingleItem();

        hosted.ShouldBeOfType<QuotaGateStartupNotice>(
            "the module registers the notice by type, so the container must be able to build it");
    }

    /// <summary>Keeps each line the notice wrote: its level, its event id and its rendered message.</summary>
    private sealed class NoticeLogger : ILogger<QuotaGateStartupNotice>
    {
        internal List<(LogLevel Level, int EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            Entries.Add((logLevel, eventId.Id, formatter(state, exception)));
        }
    }
}
