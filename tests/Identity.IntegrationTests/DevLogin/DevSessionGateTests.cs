using Evidgate.Modules.Identity.DevLogin;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.DevLogin;

/// <summary>
/// The two decisions the development sign-in rests on, each asserted on its own so that dropping a single guard
/// fails a case by name: <see cref="DevSessionGate.IsOpen"/> needs all three conditions, and
/// <see cref="DevLoginSecret.Matches"/> is a constant-time comparison of two digests.
/// </summary>
public sealed class DevSessionGateTests
{
    private const string Secret = "a-secret-only-this-test-knows";

    /// <summary>A Development host with the switch on and a secret maps the route.</summary>
    [Fact]
    public void Development_WithFlagAndSecret_IsOpen() =>
        DevSessionGate.IsOpen(Host("Development"), Options(enabled: true, Secret)).ShouldBeTrue();

    /// <summary>The host environment alone is not enough: a deployed host never maps the route.</summary>
    [Fact]
    public void Production_WithFlagAndSecret_IsClosed() =>
        DevSessionGate.IsOpen(Host("Production"), Options(enabled: true, Secret)).ShouldBeFalse();

    /// <summary>The explicit switch is required even in Development.</summary>
    [Fact]
    public void Development_WithFlagOff_IsClosed() =>
        DevSessionGate.IsOpen(Host("Development"), Options(enabled: false, Secret)).ShouldBeFalse();

    /// <summary>A missing or blank secret closes the route rather than opening it.</summary>
    /// <param name="secret">The configured value, or null/blank.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Development_WithFlagAndNoSecret_IsClosed(string? secret) =>
        DevSessionGate.IsOpen(Host("Development"), Options(enabled: true, secret)).ShouldBeFalse();

    /// <summary>The right secret matches.</summary>
    [Fact]
    public void MatchingSecret_IsAccepted() => DevLoginSecret.Matches(Secret, Secret).ShouldBeTrue();

    /// <summary>A one-character difference is refused.</summary>
    [Fact]
    public void DifferentSecret_IsRefused() =>
        DevLoginSecret.Matches(Secret, Secret + "x").ShouldBeFalse();

    /// <summary>A prefix is refused, so the comparison never accepts a truncated value.</summary>
    [Fact]
    public void PrefixOfTheSecret_IsRefused() =>
        DevLoginSecret.Matches(Secret, Secret[..^1]).ShouldBeFalse();

    /// <summary>An unset configured secret matches nothing, including a blank presentation.</summary>
    /// <param name="configured">The configured value, or null/blank.</param>
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, Secret)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void UnsetOrBlankConfiguredSecret_Matches_Nothing(string? configured, string? presented) =>
        DevLoginSecret.Matches(configured, presented).ShouldBeFalse();

    /// <summary>An absent presentation is refused, so a body without the member cannot sign in.</summary>
    [Fact]
    public void AbsentPresentation_IsRefused() => DevLoginSecret.Matches(Secret, null).ShouldBeFalse();

    /// <summary>Case matters: the value is an opaque credential, not an identifier.</summary>
    [Fact]
    public void CaseDifference_IsRefused() =>
        DevLoginSecret.Matches(Secret, Secret.ToUpperInvariant()).ShouldBeFalse();

    private static DevLoginOptions Options(bool enabled, string? secret) =>
        new() { Enabled = enabled, Secret = secret };

    private static TestHostEnvironment Host(string environmentName) => new(environmentName);

    /// <summary>The smallest <see cref="IHostEnvironment"/> the gate reads: its name.</summary>
    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Evidgate.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
