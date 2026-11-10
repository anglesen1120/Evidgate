using System.Collections.Concurrent;
using System.Data.Common;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The budget guard without a database: the reader's verdicts at every boundary, and the gateway's refusal when the read
/// itself fails. A read fault is the one refusal a real PostgreSQL makes awkward to reproduce, and the rule it protects is
/// the strictest one — an unreadable budget is refused, never treated as room.
/// </summary>
public sealed class HealingLlmGatewayRefusalTests
{
    private static readonly Guid Project = Guid.CreateVersion7();

    /// <summary>The reader's four verdicts, including both caps of the exhausted branch and the exact-cap boundary.</summary>
    /// <param name="capCost">The row's cost cap.</param>
    /// <param name="capTokens">The row's token cap.</param>
    /// <param name="usedCost">The cost spent.</param>
    /// <param name="usedTokens">The tokens spent.</param>
    /// <param name="verdict">The name of the verdict the four numbers must produce.</param>
    [Theory]
    [InlineData(1.0, 100, 0.0, 0, "Room")]
    [InlineData(1.0, 100, 0.99, 99, "Room")]
    [InlineData(1.0, 100, 1.0, 0, "Exceeded")]
    [InlineData(1.0, 100, 1.2, 0, "Exceeded")]
    [InlineData(1.0, 100, 0.0, 100, "Exceeded")]
    [InlineData(1.0, 100, 0.0, 140, "Exceeded")]
    public async Task Read_BudgetRow_AnswersItsVerdict(
        double capCost,
        int capTokens,
        double usedCost,
        int usedTokens,
        string verdict)
    {
        var (db, scopes) = Database();
        StubRow(db, new HealingLlmBudgetRow((decimal)capCost, capTokens, (decimal)usedCost, usedTokens));

        var reader = new HealingLlmBudgetReader(db, scopes);

        (await reader.ReadAsync(Project, TestContext.Current.CancellationToken))
            .ShouldBe(Enum.Parse<HealingLlmBudgetVerdict>(verdict));
    }

    /// <summary>No row for today's period is "not provisioned", never "room".</summary>
    [Fact]
    public async Task Read_AbsentRow_IsNotProvisioned()
    {
        var (db, scopes) = Database();
        StubRow(db, null);

        var reader = new HealingLlmBudgetReader(db, scopes);

        (await reader.ReadAsync(Project, TestContext.Current.CancellationToken))
            .ShouldBe(HealingLlmBudgetVerdict.NotProvisioned);
    }

    /// <summary>A read fault fails closed: an unmeasurable budget is not one with room.</summary>
    [Fact]
    public async Task Read_Fault_IsUnreadable()
    {
        var (db, scopes) = Database();
        StubFault(db);

        var reader = new HealingLlmBudgetReader(db, scopes);

        (await reader.ReadAsync(Project, TestContext.Current.CancellationToken))
            .ShouldBe(HealingLlmBudgetVerdict.Unreadable);
    }

    /// <summary>
    /// The gateway turns the unreadable verdict into a counted refusal before it compiles a schema or reaches an adapter,
    /// and writes no attempt row: nothing was attempted.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_UnreadableBudget_RefusesWithoutProviderCallOrRow()
    {
        var (db, scopes) = Database();
        StubFault(db);
        var provider = new SpyProvider();
        var gateway = new HealingLlmGateway(
            provider,
            new HealingLlmCallRecorder(db, scopes),
            Availability(),
            new HealingLlmBudgetReader(db, scopes),
            Options.Create(new HealingProviderOptions { Kind = HealingProviderKind.Http, Provider = "http" }));

        using var listener = HealingTelemetryCapture.RefusedCalls(out var reasons);
        var result = await gateway.InvokeAsync(Request(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse("an unreadable budget must refuse the call");
        result.Error!.Details!.ShouldHaveSingleItem().Reason.ShouldBe(HealingLlmRefusals.BudgetUnreadable);
        provider.Calls.ShouldBe(0);
        reasons.ToArray().ShouldBe([HealingLlmRefusals.BudgetUnreadable]);
        await db.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(),
            Arg.Any<object?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Guards run before the schema is compiled: a deployment with no provider answers <c>provider_disabled</c> even for a
    /// schema that would not compile, instead of surfacing a caller-side argument error as the outcome of a disabled call.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_NoProviderSelected_RefusesBeforeSchemaCompile()
    {
        var (db, scopes) = Database();
        var provider = new SpyProvider();
        var gateway = new HealingLlmGateway(
            provider,
            new HealingLlmCallRecorder(db, scopes),
            Availability(),
            new HealingLlmBudgetReader(db, scopes),
            Options.Create(new HealingProviderOptions { Kind = HealingProviderKind.None }));

        var result = await gateway.InvokeAsync(
            Request(responseSchema: """{"type": "not-a-json-schema-type"}"""),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Details!.ShouldHaveSingleItem().Reason.ShouldBe(HealingLlmRefusals.ProviderDisabled);
        provider.Calls.ShouldBe(0);
    }

    private static (ITenantDb Db, ITenantScopeAccessor Scopes) Database()
    {
        var db = Substitute.For<ITenantDb>();
        var scopes = Substitute.For<ITenantScopeAccessor>();
        var tenant = Substitute.For<ITenantScope>();
        tenant.OrgId.Returns(Guid.CreateVersion7());
        scopes.Require().Returns(tenant);
        return (db, scopes);
    }

    private static void StubRow(ITenantDb db, HealingLlmBudgetRow? row) =>
        db.QuerySingleOrDefaultAsync<HealingLlmBudgetRow>(
                Arg.Any<string>(),
                Arg.Any<object?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(row));

    private static void StubFault(ITenantDb db) =>
        db.QuerySingleOrDefaultAsync<HealingLlmBudgetRow>(
                Arg.Any<string>(),
                Arg.Any<object?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<HealingLlmBudgetRow?>>(_ =>
                Task.FromException<HealingLlmBudgetRow?>(new BudgetReadFault()));

    private static IHealingAvailabilityResolver Availability()
    {
        var resolver = Substitute.For<IHealingAvailabilityResolver>();
        resolver.ResolveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(HealingAvailability.Shadow));
        return resolver;
    }

    private static HealingLlmRequest Request(string? responseSchema = null) =>
        new(Project, HealingLlmPurposes.AuthoringGeneration, null, null, "healing-test-model", "v1", "payload", responseSchema);

    /// <summary>The transport-level fault the reader must refuse on.</summary>
    private sealed class BudgetReadFault() : DbException("the budget read failed");

    /// <summary>An adapter that counts calls: a refused call must never reach one.</summary>
    private sealed class SpyProvider : IHealingLlmProvider
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<HealingLlmResult> InvokeAsync(HealingLlmRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult(new HealingLlmResult(HealingLlmStatuses.Ok, """{"checklist":["open the cart"]}""", 12, 0.01m));
        }
    }
}
