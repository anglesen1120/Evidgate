using Evidgate.Api.Forwarding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// The proxy stance a start-up requires: outside Development the Api refuses to start until a proxy, a network
/// or <c>none</c> is stated, a stance that cannot parse is refused with the entry named, and Development starts
/// without one.
/// </summary>
public sealed partial class OptionsValidationTests
{
    private const string Production = "Production";

    private const string Development = "Development";

    [Fact]
    public async Task Production_Without_A_Proxy_Stance_Fails_Startup_Naming_Both_Keys()
    {
        // The shared runner states `none` as a boot baseline for every child process, so this case — the
        // absence of any stance — starts the Api itself with every EVIDGATE__ key removed from its environment.
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_ENVIRONMENT"] = Production,
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{HostLocator.FreePort()}",
            ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = DeadConnectionString,
            ["EVIDGATE__RUNS__CANCEL__SOFTSTOPDELAY"] = "00:00:30",
            ["EVIDGATE__RUNS__CANCEL__KILLDELAY"] = "00:02:00",
            [IsolationSelfCheckEnvironment.Variable] = IsolationSelfCheckEnvironment.Disabled,
        };
        var allEvidgateKeys = Environment.GetEnvironmentVariables().Keys
            .Cast<string>()
            .Where(key => key.StartsWith("EVIDGATE__", StringComparison.Ordinal))
            .Where(key => !environment.ContainsKey(key))
            .ToArray();

        using var process = ProcessRunner.Start(HostLocator.AppHost("Evidgate.Api"), environment, allEvidgateKeys);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        ProcessRunner.Result result;
        try
        {
            result = await ProcessRunner.WaitForExitAsync(process, timeout.Token);
        }
        finally
        {
            await ProcessRunner.StopAsync(process);
        }

        var output = result.Stdout + result.Stderr;
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Configuration invalid:", output, StringComparison.Ordinal);
        Assert.Contains("EVIDGATE__FORWARDEDHEADERS__KNOWNPROXIES", output, StringComparison.Ordinal);
        Assert.Contains("EVIDGATE__FORWARDEDHEADERS__KNOWNNETWORKS", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_With_A_Blank_Proxy_Stance_Is_Refused_Naming_Both_Keys()
    {
        using var factory = Boot(Production, proxies: string.Empty);

        var failure = Refusal(factory);

        failure.Message.ShouldContain("EVIDGATE__FORWARDEDHEADERS__KNOWNPROXIES");
        failure.Message.ShouldContain("EVIDGATE__FORWARDEDHEADERS__KNOWNNETWORKS");
    }

    [Fact]
    public void A_Declared_Direct_Production_Host_Passes_Validation()
    {
        using var factory = Boot(Production, proxies: TrustedProxyOptions.None);

        var stance = Stance(factory);

        stance.DeclaredDirect.ShouldBeTrue();
        stance.HasTrust.ShouldBeFalse("a direct Api adds no forwarded-headers middleware");
    }

    [Fact]
    public void A_Development_Host_Without_A_Stance_Passes_Validation()
    {
        // An empty value is what an unset variable looks like to the binder once the suite's own default is
        // overridden; Development is the one environment that admits it.
        using var factory = Boot(Development, proxies: string.Empty);

        var stance = Stance(factory);

        stance.DeclaredDirect.ShouldBeFalse();
        stance.HasTrust.ShouldBeFalse();
    }

    [Fact]
    public void A_Development_Host_With_The_Framework_Switch_And_No_Trust_Keeps_The_Framework_Options()
    {
        using var factory = Boot(Development, proxies: string.Empty, frameworkSwitch: true);

        var middleware = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        // The switch's own defaults stay: this host's configuration only overrides them when a proxy is listed.
        middleware.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedProto).ShouldBeTrue();
        middleware.KnownProxies.ShouldBeEmpty();
    }

    [Fact]
    public void A_Development_Host_With_A_Listed_Proxy_Takes_Address_And_Scheme_Only_From_It()
    {
        using var factory = Boot(Development, proxies: "203.0.113.10", frameworkSwitch: true);

        var middleware = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        // The host is never forwarded, loopback is no longer trusted, and the listed proxy replaces the defaults.
        middleware.ForwardedHeaders.ShouldBe(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
        middleware.KnownProxies.Select(p => p.ToString()).ShouldBe(["203.0.113.10"]);
    }

    [Fact]
    public void None_With_Known_Networks_Is_Refused_Naming_The_Contradiction()
    {
        using var factory = Boot(Production, proxies: TrustedProxyOptions.None, networks: "10.0.0.0/8");

        Refusal(factory).Message.ShouldContain("EVIDGATE__FORWARDEDHEADERS__KNOWNPROXIES=none contradicts");
    }

    [Fact]
    public void A_Malformed_Proxy_Entry_Is_Refused_Naming_The_Entry()
    {
        using var factory = Boot(Production, proxies: "203.0.113.10, not-an-ip");

        Refusal(factory).Message.ShouldContain(
            "EVIDGATE__FORWARDEDHEADERS__KNOWNPROXIES: 'not-an-ip' is not an IP address");
    }

    [Fact]
    public void A_Malformed_Network_Entry_Is_Refused_Naming_The_Entry()
    {
        using var factory = Boot(Production, proxies: string.Empty, networks: "203.0.113.0/24, 203.0.113.0/33");

        Refusal(factory).Message.ShouldContain(
            "EVIDGATE__FORWARDEDHEADERS__KNOWNNETWORKS: '203.0.113.0/33' is not a CIDR network");
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void A_Zero_Length_Network_Is_Refused_Because_It_Trusts_Every_Sender(string network)
    {
        using var factory = Boot(Production, proxies: string.Empty, networks: network);

        Refusal(factory).Message.ShouldContain($"'{network}' is not a CIDR network");
    }

    [Theory]
    [InlineData("10.1")]
    [InlineData("127.1")]
    public void A_Short_Form_Proxy_Address_Is_Refused(string proxy)
    {
        using var factory = Boot(Production, proxies: proxy);

        Refusal(factory).Message.ShouldContain($"'{proxy}' is not an IP address");
    }

    [Fact]
    public void None_Mixed_With_Other_Entries_Is_Refused()
    {
        using var factory = Boot(Production, proxies: "none, 203.0.113.10");

        Refusal(factory).Message.ShouldContain("'none' must stand alone");
    }

    [Fact]
    public void The_Framework_Forwarded_Headers_Switch_Is_Refused_Outside_Development()
    {
        using var factory = Boot(Production, proxies: "203.0.113.10", frameworkSwitch: true);

        Refusal(factory).Message.ShouldContain("ASPNETCORE_FORWARDEDHEADERS_ENABLED=true");
    }

    [Fact]
    public void The_Framework_Forwarded_Headers_Switch_Is_Tolerated_In_Development()
    {
        using var factory = Boot(Development, proxies: "203.0.113.10", frameworkSwitch: true);

        Stance(factory).HasTrust.ShouldBeTrue();
    }

    /// <summary>An Api host in <paramref name="environment"/> stating the proxy stance the case tests.</summary>
    /// <param name="environment">The host environment the validator reads.</param>
    /// <param name="proxies">The <c>KnownProxies</c> value; blank states no proxy, <c>none</c> a direct Api.</param>
    /// <param name="networks">The <c>KnownNetworks</c> value; blank states no network.</param>
    /// <param name="frameworkSwitch">Whether the framework's own forwarded-headers switch is on.</param>
    private static WebApplicationFactory<Program> Boot(string environment, string proxies = "", string networks = "", bool frameworkSwitch = false)
    {
        // Both keys are always written: the suite's own `none` default must never combine with a case's
        // network and be refused for a contradiction the case does not test.
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:ForwardedHeaders:KnownProxies"] = proxies,
            ["EVIDGATE:ForwardedHeaders:KnownNetworks"] = networks,
        };

        if (frameworkSwitch)
        {
            // The key the ASPNETCORE_ environment variable reads as once its prefix is stripped.
            settings["FORWARDEDHEADERS_ENABLED"] = "true";
        }

        return new EvidgateApiFactory(DeadConnectionString).WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        });
    }

    /// <summary>
    /// The stance the start-up validator judges. Resolving it runs the same validators <c>ValidateOnStart</c>
    /// runs, without a listening port or a database.
    /// </summary>
    /// <param name="factory">The host under test.</param>
    private static TrustedProxyOptions Stance(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IOptions<TrustedProxyOptions>>().Value;

    /// <summary>The refusal the start-up validator makes for <paramref name="factory"/>'s stance.</summary>
    /// <param name="factory">The host under test.</param>
    private static OptionsValidationException Refusal(WebApplicationFactory<Program> factory)
    {
        void Read() => _ = Stance(factory);

        return Should.Throw<OptionsValidationException>(Read);
    }
}
