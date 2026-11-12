using System.Collections.Concurrent;
using System.Data.Common;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The budget guard without a database: the ledger's verdict mapping, and the gateway's refusal when the reservation
/// itself fails. A statement fault is the one refusal a real PostgreSQL makes awkward to reproduce, and the rule it protects is
/// the strictest one — an unreadable budget is refused, never treated as room.
/// </summary>
public sealed class HealingLlmGatewayRefusalTests
{
    private static readonly Guid Project = Guid.CreateVersion7();
    private static readonly Guid Org = Guid.CreateVersion7();

    /// <summary>A refused-statement outcome the ledger maps to a verdict, without a database.</summary>
    [Fact]
    public async Task Reserve_Hold_IsReservedWithItsPeriod()
    {
        var (db, scopes) = Database();
        db.ScalarAsync<string?>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("2026-11-13"));

        var (verdict, reservation) = await new HealingLlmBudgetLedger(db, scopes)
            .ReserveAsync(Project, 10, 0.05m, TestContext.Current.CancellationToken);

        verdict.ShouldBe(HealingLlmBudgetVerdict.Reserved);
        reservation.ShouldBe(new HealingLlmReservation(10, 0.05m, "2026-11-13"));
    }

    [Theory]
    [InlineData(true, "Exceeded")]
    [InlineData(false, "NotProvisioned")]
    public async Task Reserve_RefusedHold_TellsSpentFromMissingRow(bool rowExists, string verdict)
    {
        var (db, scopes) = Database();
        db.ScalarAsync<string?>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        db.ScalarAsync<bool>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(rowExists));

        var (actual, reservation) = await new HealingLlmBudgetLedger(db, scopes)
            .ReserveAsync(Project, 10, 0.05m, TestContext.Current.CancellationToken);

        actual.ShouldBe(Enum.Parse<HealingLlmBudgetVerdict>(verdict));
        reservation.ShouldBeNull();
    }

    [Fact]
    public async Task Reserve_Fault_IsUnreadable()
    {
        var (db, scopes) = Database();
        db.ScalarAsync<string?>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => Task.FromException<string?>(new BudgetReadFault()));

        var (verdict, reservation) = await new HealingLlmBudgetLedger(db, scopes)
            .ReserveAsync(Project, 10, 0.05m, TestContext.Current.CancellationToken);

        verdict.ShouldBe(HealingLlmBudgetVerdict.Unreadable);
        reservation.ShouldBeNull();
    }

    [Fact]
    public async Task InvokeAsync_UnreadableBudget_RefusesWithoutProviderCallOrRow()
    {
        var (db, scopes) = Database();
        db.ScalarAsync<string?>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => Task.FromException<string?>(new BudgetReadFault()));
        var provider = new SpyProvider();
        var gateway = Gateway(provider, db, scopes, HealingProviderKind.Http);

        using var listener = HealingTelemetryCapture.RefusedCalls(out var reasons);
        var result = await gateway.InvokeAsync(Org, Request(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse("an unreadable budget must refuse the call");
        result.Error!.Details!.ShouldHaveSingleItem().Reason.ShouldBe(HealingLlmRefusals.BudgetUnreadable);
        provider.Calls.ShouldBe(0);
        reasons.ToArray().ShouldBe([HealingLlmRefusals.BudgetUnreadable]);
        await db.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(),
            Arg.Any<object?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A call cancelled before the request is sent gives the whole hold back and reaches no provider.</summary>
    [Fact]
    public async Task InvokeAsync_CancelledBeforeSend_ReleasesTheHold()
    {
        var (db, scopes) = Database();
        using var cancel = new CancellationTokenSource();
        db.ScalarAsync<string?>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await cancel.CancelAsync();
                return (string?)"2026-11-13";
            });
        var provider = new SpyProvider();
        var gateway = Gateway(provider, db, scopes, HealingProviderKind.Http);

        await Should.ThrowAsync<OperationCanceledException>(() => gateway.InvokeAsync(Org, Request(), cancel.Token));

        provider.Calls.ShouldBe(0);
        await db.DidNotReceive().ExecuteAsync(HealingLlmCallSql.Insert, Arg.Any<object?>(), Arg.Any<CancellationToken>());
        await db.Received(1).ExecuteAsync(
            HealingLlmBudgetSql.Settle,
            Arg.Any<object?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_NoProviderSelected_RefusesBeforeSchemaCompile()
    {
        var (db, scopes) = Database();
        var provider = new SpyProvider();
        var gateway = Gateway(provider, db, scopes, HealingProviderKind.None);

        var result = await gateway.InvokeAsync(
            Org,
            Request(responseSchema: """{"type": "not-a-json-schema-type"}"""),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Details!.ShouldHaveSingleItem().Reason.ShouldBe(HealingLlmRefusals.ProviderDisabled);
        provider.Calls.ShouldBe(0);
    }

    private static HealingLlmGateway Gateway(
        SpyProvider provider,
        ITenantDb db,
        ITenantScopeAccessor scopes,
        HealingProviderKind kind) =>
        new(
            provider,
            new HealingLlmCallRecorder(db, scopes),
            Availability(),
            new HealingLlmBudgetLedger(db, scopes),
            new HealingLlmTransactions((ITenantScopeFactory)scopes),
            Options.Create(new HealingProviderOptions { Kind = kind, Provider = kind.ToString().ToLowerInvariant() }),
            Options.Create(new HealingLlmOptions()),
            NullLogger<HealingLlmGateway>.Instance);

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

    private static ShadowAvailability Availability() => new();

    private sealed class ShadowAvailability : IHealingAvailabilityResolver
    {
        public ValueTask<HealingAvailability> ResolveAsync(Guid? projectId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(HealingAvailability.Shadow);
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
