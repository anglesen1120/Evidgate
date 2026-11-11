using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The V1 clamp on a resolved healing mode: a deployment whose <c>Healing:Apply</c> is off reports <c>shadow</c> for a
/// scope that stored <c>enabled</c>, and reports every other resolved mode unchanged. The helper is the one place both
/// readers (the availability resolver and the switch view's <c>Resolved</c> field) clamp, so its matrix is the contract
/// those readers inherit.
/// </summary>
public sealed class HealingApplyClampTests
{
    /// <summary>
    /// <c>enabled</c> is clamped to <c>shadow</c> exactly when the deployment may not apply healing; every other resolved
    /// mode, including the ones a clamp could never relax, is returned unchanged in both settings.
    /// </summary>
    /// <param name="resolved">The most restrictive stored mode.</param>
    /// <param name="applyEnabled">Whether the deployment may run healing in <c>enabled</c>.</param>
    /// <param name="expected">The mode in force.</param>
    [Theory]
    [InlineData(HealingAvailability.Enabled, false, HealingAvailability.Shadow)]
    [InlineData(HealingAvailability.Enabled, true, HealingAvailability.Enabled)]
    [InlineData(HealingAvailability.Shadow, false, HealingAvailability.Shadow)]
    [InlineData(HealingAvailability.Shadow, true, HealingAvailability.Shadow)]
    [InlineData(HealingAvailability.Disabled, false, HealingAvailability.Disabled)]
    [InlineData(HealingAvailability.Disabled, true, HealingAvailability.Disabled)]
    [InlineData(HealingAvailability.Unavailable, false, HealingAvailability.Unavailable)]
    [InlineData(HealingAvailability.Unavailable, true, HealingAvailability.Unavailable)]
    public void Apply_ClampsOnlyEnabled(
        HealingAvailability resolved,
        bool applyEnabled,
        HealingAvailability expected)
    {
        HealingApplyClamp.Apply(resolved, applyEnabled).ShouldBe(expected);
    }
}
