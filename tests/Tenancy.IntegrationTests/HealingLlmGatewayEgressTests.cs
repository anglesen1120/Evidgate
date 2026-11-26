using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// What the gateway does with the redaction step: the provider receives only masked text, the step runs after the
/// availability and budget guards and before the send, and a payload that still holds a secret-shaped value is not
/// sent, not billed and recorded as <c>blocked_leak</c> with the hash of its report.
/// </summary>
/// <remarks>No database: the recorder runs over a substituted <see cref="ITenantDb"/>, so a row is read as an issued statement.</remarks>
public sealed class HealingLlmGatewayEgressTests
{
    private static readonly Guid Project = Guid.CreateVersion7();
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly string Password = "hunter2" + "hunter2";

    [Fact]
    public async Task InvokeAsync_SendsOnlyTheMaskedPayloadAndStoresItsReportHash()
    {
        var (db, scopes) = Database();
        var provider = new SpyProvider();
        var gateway = Gateway(db, scopes, provider, new RedactionPipeline(), HealingAvailability.Shadow);

        var result = await gateway.InvokeAsync(
            Org, Request("log: password=" + Password + " for jane.doe" + "@example.com"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        provider.Payloads.ShouldHaveSingleItem().ShouldNotContain(Password);
        provider.Payloads[0].ShouldNotContain("jane.doe");
        provider.Payloads[0].ShouldContain("[redacted:email]");

        var row = AttemptRows(db).ShouldHaveSingleItem();
        row.Sql.ShouldBe(HealingLlmCallSql.InsertWithReport);
        row.Value("status").ShouldBe(HealingLlmStatuses.Ok);
        var report = row.Value("report").ShouldBeOfType<string>();
        report.ShouldNotContain(Password);
        report.ShouldNotContain("jane.doe");
        RedactionReport.Matches(report, row.Value("report_hash").ShouldBeOfType<string>()).ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeAsync_ResidualAfterMasking_SendsNothingAndRecordsBlockedLeak()
    {
        var (db, scopes) = Database();
        var provider = new SpyProvider();
        var gateway = Gateway(db, scopes, provider, new RedactionPipeline(), HealingAvailability.Shadow);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("password=" + Password));

        var result = await gateway.InvokeAsync(Org, Request("note " + encoded + " end"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value!.Result.Status.ShouldBe(HealingLlmStatuses.BlockedLeak);
        result.Value.Result.Content.ShouldBeNull();
        result.Value.Result.Tokens.ShouldBe(0);
        result.Value.Result.Cost.ShouldBe(0m);
        provider.Calls.ShouldBe(0, "a blocked payload must never reach the provider");

        var row = AttemptRows(db).ShouldHaveSingleItem();
        row.Value("status").ShouldBe(HealingLlmStatuses.BlockedLeak);
        row.Value("tokens").ShouldBe(0);
        row.Value("cost").ShouldBe(0m);
        row.Value("report_hash").ShouldBeOfType<string>().ShouldStartWith("sha256:");
        row.Value("report").ShouldBeOfType<string>().ShouldNotContain(Password);
    }

    [Fact]
    public async Task InvokeAsync_RedactsAfterTheAvailabilityGuardAndBeforeTheSend()
    {
        var order = new List<string>();
        var (db, scopes) = Database();
        var provider = new SpyProvider(order);
        var pipeline = new OrderingPipeline(order);

        var refused = await Gateway(db, scopes, provider, pipeline, HealingAvailability.Unavailable)
            .InvokeAsync(Org, Request("payload"), TestContext.Current.CancellationToken);
        refused.IsSuccess.ShouldBeFalse();
        order.ShouldBeEmpty("the availability guard refuses before anything is masked or sent");

        var placed = await Gateway(db, scopes, provider, pipeline, HealingAvailability.Shadow)
            .InvokeAsync(Org, Request("payload"), TestContext.Current.CancellationToken);
        placed.IsSuccess.ShouldBeTrue(placed.Error?.Message);
        order.ShouldBe(["redact", "send"]);
    }

    [Fact]
    public async Task InvokeAsync_PipelineFault_SendsNothingAndRecordsNoRow()
    {
        var (db, scopes) = Database();
        var provider = new SpyProvider();
        var gateway = Gateway(db, scopes, provider, new ThrowingPipeline(), HealingAvailability.Shadow);

        await Should.ThrowAsync<InvalidOperationException>(
            () => gateway.InvokeAsync(Org, Request("payload"), TestContext.Current.CancellationToken));

        provider.Calls.ShouldBe(0);
        AttemptRows(db).ShouldBeEmpty();
    }

    [Fact]
    public void Recordable_ProviderReportedBlockedLeak_IsAnError()
    {
        var recorded = HealingLlmResultRules.Recordable(new HealingLlmResult(HealingLlmStatuses.BlockedLeak, null, 5, 0.1m));

        recorded.Status.ShouldBe(HealingLlmStatuses.Error);
    }

    private static HealingLlmGateway Gateway(
        ITenantDb db,
        ITenantScopeAccessor scopes,
        IHealingLlmProvider provider,
        IRedactionPipeline pipeline,
        HealingAvailability availability)
    {
        db.ScalarAsync<string?>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("2026-11-13"));
        return new HealingLlmGateway(
            provider,
            new HealingLlmCallRecorder(db, scopes),
            new FixedAvailability(availability),
            new HealingLlmBudgetLedger(db, scopes),
            new HealingLlmTransactions((ITenantScopeFactory)scopes),
            Options.Create(new HealingProviderOptions { Kind = HealingProviderKind.Mock, Provider = "mock" }),
            Options.Create(new HealingLlmOptions()),
            pipeline,
            NullLogger<HealingLlmGateway>.Instance);
    }

    private static (ITenantDb Db, ITenantScopeAccessor Scopes) Database()
    {
        var db = Substitute.For<ITenantDb>();
        var factory = Substitute.For<ITenantScopeFactory>();
        var tenant = Substitute.For<ITenantScope>();
        tenant.OrgId.Returns(Org);
        factory.Current.Returns((ITenantScope?)null);
        factory.Require().Returns(tenant);
        factory.BeginAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(tenant));
        return (db, factory);
    }

    private static HealingLlmRequest Request(string payload) =>
        new(Project, HealingLlmPurposes.AuthoringGeneration, null, null, "healing-test-model", "v1", payload, null);

    /// <summary>The attempt-row statements the substituted database received (the call ledger's two shapes).</summary>
    private static List<Statement> AttemptRows(ITenantDb db) =>
        db.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ITenantDb.ExecuteAsync))
            .Select(call => call.GetArguments())
            .Where(args => args[0] is string sql
                && (sql == HealingLlmCallSql.Insert || sql == HealingLlmCallSql.InsertWithReport))
            .Select(args => new Statement((string)args[0]!, args[1]!))
            .ToList();

    private sealed record Statement(string Sql, object Parameters)
    {
        internal object? Value(string name) => Parameters.GetType().GetProperty(name)!.GetValue(Parameters);
    }

    private sealed class FixedAvailability(HealingAvailability availability) : IHealingAvailabilityResolver
    {
        public ValueTask<HealingAvailability> ResolveAsync(Guid? projectId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(availability);
    }

    private sealed class OrderingPipeline(List<string> order) : IRedactionPipeline
    {
        public RedactionOutcome Redact(string payload)
        {
            order.Add("redact");
            return new RedactionPipeline().Redact(payload);
        }
    }

    private sealed class ThrowingPipeline : IRedactionPipeline
    {
        public RedactionOutcome Redact(string payload) => throw new InvalidOperationException("pipeline fault");
    }

    private sealed class SpyProvider(List<string>? order = null) : IHealingLlmProvider
    {
        private int _calls;

        internal List<string> Payloads { get; } = [];

        internal int Calls => Volatile.Read(ref _calls);

        public Task<HealingLlmResult> InvokeAsync(HealingLlmRequest request, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _calls);
            order?.Add("send");
            Payloads.Add(request.Payload);
            return Task.FromResult(new HealingLlmResult(
                HealingLlmStatuses.Ok,
                """{"checklist":["open the cart"]}""",
                12,
                0.01m));
        }
    }
}
