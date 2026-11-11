using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The availability guard at the one mode the persisted switch cannot hold today:
/// <see cref="HealingAvailability.Unavailable"/>, an observation only a health probe produces. The gateway must refuse it
/// exactly as it refuses a stored <c>disabled</c>, and the same composition answering <c>shadow</c> must proceed, so the
/// resolver's value — not the rest of the world — is what decides.
/// </summary>
/// <remarks>
/// No database: the recorder and the budget reader run over a substituted <see cref="ITenantDb"/>, so "no call row" is
/// read as "no insert was issued", and the budget row with room answers the guard that runs after the availability one.
/// </remarks>
public sealed class HealingLlmGatewayAvailabilityTests
{
    private static readonly Guid Project = Guid.CreateVersion7();

    /// <summary>
    /// An unavailable provider refuses as <c>healing_disabled</c> before the budget is read or the adapter is asked: no
    /// provider call, no attempt row, and exactly one refusal measurement.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_Unavailable_RefusesWithoutProviderCallOrRow()
    {
        var (db, scopes) = Database();
        StubBudgetRow(db);
        var provider = new SpyProvider();
        var gateway = Gateway(db, scopes, provider, HealingAvailability.Unavailable);

        using var listener = HealingTelemetryCapture.RefusedCalls(out var reasons);
        var result = await gateway.InvokeAsync(Request(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse("an unavailable healing provider must refuse the call");
        result.Error!.Details!.ShouldHaveSingleItem().Reason.ShouldBe(HealingLlmRefusals.HealingDisabled);
        provider.Calls.ShouldBe(0);
        await db.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(),
            Arg.Any<object?>(),
            Arg.Any<CancellationToken>());
        reasons.ToArray().ShouldBe([HealingLlmRefusals.HealingDisabled]);
    }

    /// <summary>
    /// The positive control: the identical composition answering <c>shadow</c> places the call and records one attempt,
    /// with nothing counted as refused.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_Shadow_PlacesTheCallAndRecordsIt()
    {
        var (db, scopes) = Database();
        StubBudgetRow(db);
        var provider = new SpyProvider();
        var gateway = Gateway(db, scopes, provider, HealingAvailability.Shadow);

        using var listener = HealingTelemetryCapture.RefusedCalls(out var reasons);
        var result = await gateway.InvokeAsync(Request(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        provider.Calls.ShouldBe(1);
        await db.Received(1).ExecuteAsync(
            Arg.Any<string>(),
            Arg.Any<object?>(),
            Arg.Any<CancellationToken>());
        reasons.ShouldBeEmpty();
    }

    /// <summary>The gateway over substituted tenants, with the provider and the availability its case varies.</summary>
    private static HealingLlmGateway Gateway(
        ITenantDb db,
        ITenantScopeAccessor scopes,
        IHealingLlmProvider provider,
        HealingAvailability availability) =>
        new(
            provider,
            new HealingLlmCallRecorder(db, scopes),
            new FixedAvailability(availability),
            new HealingLlmBudgetReader(db, scopes),
            Options.Create(new HealingProviderOptions { Kind = HealingProviderKind.Mock, Provider = "mock" }));

    private static (ITenantDb Db, ITenantScopeAccessor Scopes) Database()
    {
        var db = Substitute.For<ITenantDb>();
        var scopes = Substitute.For<ITenantScopeAccessor>();
        var tenant = Substitute.For<ITenantScope>();
        tenant.OrgId.Returns(Guid.CreateVersion7());
        scopes.Require().Returns(tenant);
        return (db, scopes);
    }

    /// <summary>A budget row with room under both caps, so only availability can refuse.</summary>
    private static void StubBudgetRow(ITenantDb db) =>
        db.QuerySingleOrDefaultAsync<HealingLlmBudgetRow>(
                Arg.Any<string>(),
                Arg.Any<object?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<HealingLlmBudgetRow?>(new HealingLlmBudgetRow(1.0m, 100, 0.4m, 40)));

    private static HealingLlmRequest Request() =>
        new(Project, HealingLlmPurposes.AuthoringGeneration, null, null, "healing-test-model", "v1", "payload", null);

    /// <summary>The resolver at one fixed answer, so only its value differs between the two cases.</summary>
    private sealed class FixedAvailability(HealingAvailability availability) : IHealingAvailabilityResolver
    {
        public ValueTask<HealingAvailability> ResolveAsync(Guid? projectId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(availability);
    }

    /// <summary>An adapter that counts calls and answers schema-valid content.</summary>
    private sealed class SpyProvider : IHealingLlmProvider
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<HealingLlmResult> InvokeAsync(HealingLlmRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult(new HealingLlmResult(
                HealingLlmStatuses.Ok,
                """{"checklist":["open the cart"]}""",
                12,
                0.01m));
        }
    }
}
