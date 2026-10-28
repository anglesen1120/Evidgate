using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Healing.Application;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 08.6.3 AC7 (the V1 clamp) and AC2's refusal vocabulary as the unit under test: the one gate both the tenant route
/// and the operator's system-scope writer use. With <c>Healing:Apply</c> off (the default in every shipped
/// configuration) <c>enabled</c> is 422 <c>mode_not_available</c> and the other two storable modes pass; with it on,
/// <c>enabled</c> passes; <c>unavailable</c> is refused with 422 <c>invalid_switch_mode</c> in both cases.
/// </summary>
/// <remarks>
/// The gate is the clamp's only implementation, so a flag-off/flag-on pair on it covers every writer: the tenant
/// handler and <c>HealingSystemSwitchWriter</c> both call it before they touch a connection.
/// </remarks>
public sealed class V1ModeClampTests
{
    /// <summary>AC7: the clamp matrix — one row per (mode, Healing:Apply) pair that matters.</summary>
    /// <param name="mode">The requested mode.</param>
    /// <param name="apply">The <c>Healing:Apply</c> value.</param>
    /// <param name="expectedCode">The error code, or null when the pair is accepted.</param>
    [Theory]
    [InlineData("enabled", false, ApiErrorCode.ModeNotAvailable)]
    [InlineData("enabled", true, null)]
    [InlineData("shadow", false, null)]
    [InlineData("shadow", true, null)]
    [InlineData("disabled", false, null)]
    [InlineData("disabled", true, null)]
    [InlineData("unavailable", false, ApiErrorCode.InvalidSwitchMode)]
    [InlineData("unavailable", true, ApiErrorCode.InvalidSwitchMode)]
    [InlineData("enforcing", true, ApiErrorCode.InvalidSwitchMode)]
    [InlineData("", true, ApiErrorCode.InvalidSwitchMode)]
    [InlineData(null, true, ApiErrorCode.InvalidSwitchMode)]
    public void The_v1_clamp_and_the_mode_vocabulary(string? mode, bool apply, string? expectedCode)
    {
        var error = HealingModeGate.Check(mode, reason: null, applyEnabled: apply);

        if (expectedCode is null)
        {
            error.ShouldBeNull();
            return;
        }

        error.ShouldNotBeNull();
        error!.Code.ShouldBe(expectedCode);
        error.Status.ShouldBe(422);
    }

    /// <summary>AC7: the clamp's refusal names the mode field, so a client can point at it.</summary>
    [Fact]
    public void The_clamp_refusal_names_the_mode_field()
    {
        var error = HealingModeGate.Check("enabled", reason: null, applyEnabled: false);

        error.ShouldNotBeNull();
        Details(error).ShouldContain(detail => detail.Path == "mode" && detail.Reason == "not_available");
    }

    /// <summary>The reason bound of the column (<c>healing_switch_reason_length_check</c>): 1–2000 characters.</summary>
    [Fact]
    public void The_reason_is_bounded_by_the_column()
    {
        HealingModeGate.Check("shadow", new string('a', 2000), applyEnabled: false).ShouldBeNull();
        HealingModeGate.Check("shadow", null, applyEnabled: false).ShouldBeNull();

        Details(HealingModeGate.Check("shadow", new string('a', 2001), applyEnabled: false))
            .ShouldContain(detail => detail.Path == "reason" && detail.Reason == "too_long");
        Details(HealingModeGate.Check("shadow", string.Empty, applyEnabled: false))
            .ShouldContain(detail => detail.Path == "reason" && detail.Reason == "empty");
        Details(HealingModeGate.Check("shadow", "a\0b", applyEnabled: false))
            .ShouldContain(detail => detail.Path == "reason" && detail.Reason == "invalid_value");
    }

    /// <summary>The details of a refusal, which every gate answer in these tests carries.</summary>
    private static IReadOnlyList<Evidgate.BuildingBlocks.SharedKernel.Api.ErrorDetail> Details(
        Evidgate.BuildingBlocks.SharedKernel.Results.Error? error)
    {
        error.ShouldNotBeNull();
        return error!.Details ?? [];
    }
}
