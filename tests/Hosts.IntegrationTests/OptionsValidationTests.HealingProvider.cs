using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Healing.Application;
using Evidgate.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// The provider a deployment selects. Outside Development it must be stated and may not be the mock; an HTTP selection
/// needs a base address the adapter can post under and a secret name the secret provider declares. A Development host may
/// run on the unset default, which is the offline adapter, and an unset provider anywhere else refuses the boot.
/// </summary>
public sealed partial class OptionsValidationTests
{
    [Fact]
    public async Task Production_Without_A_Provider_Fails_Startup_Naming_The_Key()
    {
        // The suite states `none` as a boot baseline for every host it starts, so this case — the absence of any
        // selection — starts the Api with the healing keys removed from its environment. The proxy stance stays,
        // because the Api reads it first and would refuse the boot for that key instead.
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{HostLocator.FreePort()}",
            ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = DeadConnectionString,
            ["EVIDGATE__RUNS__CANCEL__SOFTSTOPDELAY"] = "00:00:30",
            ["EVIDGATE__RUNS__CANCEL__KILLDELAY"] = "00:02:00",
            [IsolationSelfCheckEnvironment.Variable] = IsolationSelfCheckEnvironment.Disabled,
            [NoForwardedProxy.Variable] = "none",
        };
        var inherited = Environment.GetEnvironmentVariables().Keys
            .Cast<string>()
            .Where(key => key.StartsWith("EVIDGATE__", StringComparison.Ordinal))
            .Where(key => !string.Equals(key, NoForwardedProxy.Variable, StringComparison.Ordinal))
            .ToArray();

        using var process = ProcessRunner.Start(HostLocator.AppHost("Evidgate.Api"), environment, inherited);
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
        Assert.Contains("EVIDGATE__HEALING__LLM__PROVIDER", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A selection is judged at boot: the environment decides whether the word may run, and the HTTP adapter refuses a
    /// base address it cannot post under or a secret name the secret provider does not declare.
    /// </summary>
    /// <param name="environment">The host environment the selection is judged in.</param>
    /// <param name="provider">The <c>Provider</c> value, or null to leave it unset.</param>
    /// <param name="baseAddress">The <c>BaseAddress</c> value, or null to leave it unset.</param>
    /// <param name="apiKeySecret">The <c>ApiKeySecret</c> value, or null to leave it unset.</param>
    /// <param name="named">The key the refusal must name, or null when the selection must pass.</param>
    [Theory]
    [InlineData("Production", null, null, null, "EVIDGATE__HEALING__LLM__PROVIDER")]
    [InlineData("Production", "", null, null, "EVIDGATE__HEALING__LLM__PROVIDER")]
    [InlineData("Production", "mock", null, null, "EVIDGATE__HEALING__LLM__PROVIDER")]
    [InlineData("Production", "bogus", null, null, "EVIDGATE__HEALING__LLM__PROVIDER")]
    [InlineData("Production", "none", null, null, null)]
    [InlineData("Production", "http", null, null, "EVIDGATE__HEALING__LLM__BASEADDRESS")]
    [InlineData("Production", "http", "provider.invalid/v1/", null, "EVIDGATE__HEALING__LLM__BASEADDRESS")]
    [InlineData("Production", "http", "http://provider.invalid/v1/", null, "EVIDGATE__HEALING__LLM__BASEADDRESS")]
    [InlineData("Production", "http", "https://provider.invalid/v1", null, "EVIDGATE__HEALING__LLM__BASEADDRESS")]
    [InlineData("Production", "http", "https://provider.invalid/v1/?tenant=a", null, "EVIDGATE__HEALING__LLM__BASEADDRESS")]
    [InlineData("Production", "http", "https://provider.invalid/v1/", null, "EVIDGATE__HEALING__LLM__APIKEYSECRET")]
    [InlineData("Production", "http", "https://provider.invalid/v1/", "not-a-secret-name", "EVIDGATE__HEALING__LLM__APIKEYSECRET")]
    [InlineData("Development", null, null, null, null)]
    [InlineData("Development", "none", null, null, null)]
    [InlineData("Development", "http", "http://provider.invalid/", SecretNames.HealingLlmApiKey, null)]
    public void A_Provider_Selection_Is_Validated_At_Boot(
        string environment,
        string? provider,
        string? baseAddress,
        string? apiKeySecret,
        string? named)
    {
        using var factory = BootProvider(environment, provider, baseAddress, apiKeySecret);

        if (named is null)
        {
            _ = ProviderOptions(factory);
            return;
        }

        ProviderRefusal(factory).Message.ShouldContain(named);
    }

    [Fact]
    public void A_Development_Host_With_No_Provider_Runs_The_Offline_Adapter()
    {
        using var factory = BootProvider("Development", provider: null);

        ProviderOptions(factory).Kind.ShouldBe(HealingProviderKind.Mock);
    }

    [Fact]
    public void A_Production_Host_Stating_None_Runs_No_Adapter()
    {
        using var factory = BootProvider("Production", provider: "none");

        ProviderOptions(factory).Kind.ShouldBe(HealingProviderKind.None);
    }

    [Fact]
    public void A_Production_Host_With_A_Complete_Http_Selection_Runs_The_Http_Adapter()
    {
        using var factory = BootProvider("Production", "http", "https://provider.invalid/v1/", SecretNames.HealingLlmApiKey);

        var selection = ProviderOptions(factory);

        selection.Kind.ShouldBe(HealingProviderKind.Http);
        selection.ApiKeySecret.ShouldBe(SecretNames.HealingLlmApiKey);
    }

    /// <summary>An Api host in <paramref name="environment"/> stating the provider selection a case tests.</summary>
    /// <param name="environment">The host environment the validator reads.</param>
    /// <param name="provider">The <c>Provider</c> value, or null to leave it unset.</param>
    /// <param name="baseAddress">The <c>BaseAddress</c> value, or null to leave it unset.</param>
    /// <param name="apiKeySecret">The <c>ApiKeySecret</c> value, or null to leave it unset.</param>
    private static WebApplicationFactory<Program> BootProvider(
        string environment,
        string? provider,
        string? baseAddress = null,
        string? apiKeySecret = null)
    {
        // Every key is written, so neither the suite's `none` baseline nor a case's leftover decides another case: an
        // empty value is what an unset key looks like once it is overridden.
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:Healing:Llm:Provider"] = provider ?? string.Empty,
            ["EVIDGATE:Healing:Llm:BaseAddress"] = baseAddress ?? string.Empty,
            ["EVIDGATE:Healing:Llm:ApiKeySecret"] = apiKeySecret ?? string.Empty,
        };

        return new EvidgateApiFactory(DeadConnectionString).WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        });
    }

    /// <summary>
    /// The selection the boot judges. Resolving it runs the same validators <c>ValidateOnStart</c> runs, without a
    /// listening port or a database.
    /// </summary>
    /// <param name="factory">The host under test.</param>
    private static HealingProviderOptions ProviderOptions(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IOptions<HealingProviderOptions>>().Value;

    /// <summary>The refusal the boot validator makes for <paramref name="factory"/>'s selection.</summary>
    /// <param name="factory">The host under test.</param>
    private static OptionsValidationException ProviderRefusal(WebApplicationFactory<Program> factory)
    {
        void Read() => _ = ProviderOptions(factory);

        return Should.Throw<OptionsValidationException>(Read);
    }
}
