// The advisory quota read and the transaction month: a peek blocks only on a counter of the month that has no room,
// writes and carries nothing, and a question that names no month is gated in the caller's own transaction.
using Evidgate.BuildingBlocks.Persistence.Tenancy;
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
/// The read-only half of the gate against a real PostgreSQL: a month whose counter has no room answers Blocked with the
/// wait, the month and the refusing scope, a month with room or with no counter at all answers Clear, and neither answer
/// writes a counter, stamps one or carries a limit forward.
/// </summary>
[Trait("Category", "Docker")]
public sealed class UsageQuotaPeekTests : IClassFixture<TenancyHostPool>
{
    private const string Period = "2026-10";
    private const string PreviousPeriod = "2026-09";
    private const decimal Limit = 10m;
    private const decimal WarnRatio = 0.8m;

    private readonly PostgresTenancyFixture _fixture;
    // Reuse one API host per class; each world still owns its tenant data.
    // Dedicated databases and overlapping worlds keep private hosts.
    private readonly TenancyHostPool _hosts;

    public UsageQuotaPeekTests(PostgresTenancyFixture fixture, TenancyHostPool hosts)
    {
        _fixture = fixture;
        _hosts = hosts;
    }

    private static QuotaCheckRequest Request(
        UsageQuotaWorld world,
        decimal quantity,
        string? period = Period,
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
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        await SeedAsync(world, used: Limit, projectId: world.Project);

        var blocked = await world.PeekAsync(Request(world, 1m));

        blocked.Blocked.ShouldBeTrue();
        blocked.Used.ShouldBe(Limit);
        blocked.Limit.ShouldBe(Limit);
        blocked.RetryAfter.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        blocked.RaisePath.ShouldBe($"/internal/operator/billing/orgs/{world.Org:D}/quota-raise");
        blocked.Period.ShouldBe(Period, "a blocked answer names the month the counter refused in");

        // The project's own counter is the one that refused: the tenant scope was not named.
        blocked.RefusedScope.ShouldBe(QuotaScope.Project, "the project's counter of the period has no room");
        (await world.CounterCountAsync()).ShouldBe(1, "a peek creates no counter");
    }

    [Fact]
    public async Task QuotaGatePeek_BlockedCounter_ReportsWhetherItsStampIsStillUnwritten()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        var counterId = await SeedAsync(world, used: Limit, projectId: world.Project);

        var fresh = await world.PeekAsync(Request(world, 1m));

        fresh.Blocked.ShouldBeTrue();
        fresh.Unstamped.ShouldBeTrue("the counter reached the refusal without a stamp of its own");
        QuotaPeekResult.Clear.Unstamped.ShouldBeFalse("a clear answer names no counter a caller could stamp");

        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.quota_counter set blocked_at = now() where id = '{counterId:D}'");

        var stamped = await world.PeekAsync(Request(world, 1m));

        stamped.Blocked.ShouldBeTrue("the stamp frees no room, so the counter still refuses");
        stamped.Unstamped.ShouldBeFalse(
            "a caller must not open a stamp transaction for a row that already carries the stamp");
    }

    [Fact]
    public async Task QuotaGatePeek_OnlyTheSecondRefusingScopeUnstamped_StillReportsUnstamped()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        var tenantId = await SeedAsync(world, used: Limit);
        var projectId = await SeedAsync(world, used: Limit, projectId: world.Project);
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.quota_counter set blocked_at = now() where id = '{tenantId:D}'");
        var request = Request(world, 1m, includeTenantScope: true);

        var secondUnstamped = await world.PeekAsync(request);

        secondUnstamped.Blocked.ShouldBeTrue();
        secondUnstamped.RefusedScope.ShouldBe(QuotaScope.Tenant, "the organization counter is the first that refuses");
        secondUnstamped.Unstamped.ShouldBeTrue(
            "the project refuses too and carries no stamp, so a stamp write would still change a row");

        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.quota_counter set blocked_at = now() where id = '{projectId:D}'");

        (await world.PeekAsync(request)).Unstamped.ShouldBeFalse("every refusing scope already carries its stamp");
    }

    [Fact]
    public async Task QuotaGatePeek_TenantCounterFull_BlocksTenantScope()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        await SeedAsync(world, used: Limit, projectId: null);
        await SeedAsync(world, used: 0m, projectId: world.Project);

        var bothScopes = await world.PeekAsync(Request(world, 1m, includeTenantScope: true));

        bothScopes.Blocked.ShouldBeTrue();
        bothScopes.RefusedScope.ShouldBe(QuotaScope.Tenant, "the organization-wide counter is the first that refuses");

        // The positive control: the project's own counter has room, so the same call without the tenant scope proceeds.
        var projectOnly = await world.PeekAsync(Request(world, 1m));

        projectOnly.Blocked.ShouldBeFalse();
    }

    [Fact]
    public async Task QuotaGatePeek_ForeignOrgFullCounters_DoNotBlock()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await SeedAsync(world, used: Limit, projectId: otherProject, org: otherOrg);
        await SeedAsync(world, used: Limit, projectId: null, org: otherOrg);
        await SeedAsync(world, used: 0m, projectId: world.Project);
        var foreignProject = await world.CounterAsync(org: otherOrg, project: otherProject);
        var foreignTenant = await world.CounterAsync(org: otherOrg);

        var projectOnly = await world.PeekAsync(Request(world, 1m));

        projectOnly.Blocked.ShouldBeFalse("another organization's full counter refuses nothing here");
        var bothScopes = await world.PeekAsync(Request(world, 1m, includeTenantScope: true));

        bothScopes.Blocked.ShouldBeFalse(
            "the same period, metric and tenant scope under another organization is a different counter");

        // The positive control: the same two counters under this organization refuse the same question.
        await SeedAsync(world, used: Limit, projectId: null);
        var refused = await world.PeekAsync(Request(world, 1m, includeTenantScope: true));

        refused.Blocked.ShouldBeTrue();
        refused.RefusedScope.ShouldBe(QuotaScope.Tenant, "this organization's own full tenant counter refuses");
        (await world.CounterAsync(org: otherOrg, project: otherProject)).ShouldBe(foreignProject);
        (await world.CounterAsync(org: otherOrg)).ShouldBe(foreignTenant);
    }

    // ------------------------------------------------------------------ the admission

    [Fact]
    public async Task QuotaGatePeek_RoomOrExactFit_ReturnsClear()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        await SeedAsync(world, used: 9m, projectId: world.Project);

        var exactFit = await world.PeekAsync(Request(world, 1m));
        exactFit.Blocked.ShouldBeFalse("the last unit inside the limit still fits");
        exactFit.Used.ShouldBeNull("a clear answer carries no numbers a caller could read as an admission");

        var overTheLine = await world.PeekAsync(Request(world, 2m));
        overTheLine.Blocked.ShouldBeTrue("the same counter has no room for two units");
    }

    [Fact]
    public async Task QuotaGatePeek_NoCounterOfThePeriod_ReturnsClearWithoutCarry()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        await SeedAsync(world, used: Limit, period: PreviousPeriod, projectId: world.Project);

        var answer = await world.PeekAsync(Request(world, 1m));

        answer.Blocked.ShouldBeFalse("a period without a counter has no configured limit to refuse with");
        answer.Period.ShouldBeNull("no counter answered, so no month answers either");
        (await world.CounterCountAsync()).ShouldBe(1, "the earlier month's limit is not carried into this period");
        (await world.CounterIdAsync(project: world.Project, period: Period)).ShouldBeNull();
        (await world.CounterAsync(project: world.Project, period: PreviousPeriod))
            .ShouldNotBeNull().Used.ShouldBe(Limit, "the earlier month's counter is not written either");
    }

    // ------------------------------------------------------------------ it reads, it never writes

    [Fact]
    public async Task QuotaGatePeek_Block_LeavesTheCounterUntouched()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        await SeedAsync(world, used: 5m, projectId: world.Project);
        var before = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        world.ClearStatementLog();

        var blocked = await world.PeekAsync(Request(world, 6m));

        blocked.Blocked.ShouldBeTrue();
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
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        await SeedAsync(world, used: 0m, projectId: world.Project);
        world.ClearStatementLog();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => world.PeekAsync(Request(world, 1m, period: "2026-13")));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => world.PeekAsync(Request(world, 0m)));

        world.CounterStatements().ShouldBeEmpty("a caller bug is refused before any statement runs");
    }

    [Fact]
    public async Task QuotaGatePeek_Disabled_ReturnsClearWithoutRead()
    {
        await using var world = await UsageQuotaWorld.StartAsync(
            _fixture,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Usage:QuotaGate:Enabled"] = "false",
            }, hosts: _hosts);
        await SeedAsync(world, used: Limit, projectId: world.Project, blockedAt: DateTimeOffset.UnixEpoch);
        var before = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        world.ClearStatementLog();

        var answer = await world.PeekAsync(Request(world, 1m, period: null));

        answer.ShouldBe(QuotaPeekResult.Clear, "the operator switch answers Clear explicitly, it is not a stub");
        answer.Period.ShouldBeNull();
        world.CounterStatements().ShouldBeEmpty("a switched-off gate does not read a counter");

        var after = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        after.Used.ShouldBe(before.Used);
        after.Version.ShouldBe(before.Version);
        after.BlockedAt.ShouldBe(before.BlockedAt);
    }

    // ------------------------------------------------------------------ a question that names no month

    [Fact]
    public async Task QuotaGatePeek_NullPeriod_BlocksAndNamesTheTransactionsMonth()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        var token = TestContext.Current.CancellationToken;
        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        await using var scope = await provider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, token);

        // transaction_timestamp() is fixed for the whole transaction, so the month read here is the one the gate's own
        // statement resolves in this same transaction.
        var month = (await provider.GetRequiredService<ITenantDb>().QuerySingleOrDefaultAsync<string>(
            "select to_char(transaction_timestamp() at time zone 'UTC','YYYY-MM')", cancellationToken: token))!;
        await SeedAsync(world, used: Limit, period: month, projectId: world.Project);

        var blocked = await provider.GetRequiredService<IQuotaGate>()
            .PeekAsync(Request(world, 1m, period: null), token);
        await scope.CommitAsync(token);

        blocked.Blocked.ShouldBeTrue("the month's counter has no room");
        blocked.Period.ShouldBe(month, "the statement resolved the month of its own transaction");
        blocked.Used.ShouldBe(Limit);
        blocked.Limit.ShouldBe(Limit);
        blocked.RetryAfter.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        blocked.RefusedScope.ShouldBe(QuotaScope.Project);
        (await world.CounterCountAsync()).ShouldBe(1, "a peek creates no counter");
    }

    [Fact]
    public async Task QuotaGatePeek_NullPeriod_NoCounterOrRoomLeft_IsClear()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);

        var noCounter = await world.PeekAsync(Request(world, 1m, period: null));

        noCounter.Blocked.ShouldBeFalse("a month without a counter has no configured limit to refuse with");
        noCounter.Period.ShouldBeNull();

        var (month, _) = await world.DatabaseMonthsAsync();
        await SeedAsync(world, used: 0m, period: month, projectId: world.Project);
        var roomLeft = await world.PeekAsync(Request(world, 1m, period: null));

        roomLeft.Blocked.ShouldBeFalse("the counter has room for the call");
        roomLeft.Period.ShouldBeNull("only a blocked answer names a month");
        (await world.CounterCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task QuotaGatePeek_NullPeriod_IsOneStatement()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        var (month, _) = await world.DatabaseMonthsAsync();
        await SeedAsync(world, used: 0m, period: month, projectId: world.Project);
        world.ClearStatementLog();

        var answer = await world.PeekAsync(Request(world, 1m, period: null));

        answer.Blocked.ShouldBeFalse();
        world.CounterStatements().ShouldHaveSingleItem();
        ClockReads(world).ShouldBe(
            1,
            "the month is resolved inside the statement that reads the counter, not by another round trip");
    }

    /// <summary>How many statements the host sent that read the transaction's clock, whatever else they say.</summary>
    /// <param name="world">The world whose Api host recorded the statements.</param>
    private static int ClockReads(UsageQuotaWorld world) =>
        world.Api.Statements.Snapshot().Count(statement =>
            statement.Contains("to_char(transaction_timestamp()", StringComparison.Ordinal));
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
        entry.EventId.ShouldBe(2476);
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
        entry.EventId.ShouldBe(2475);
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
