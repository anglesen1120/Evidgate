using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Healing;
using Evidgate.Modules.Healing.Application;
using Evidgate.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// The provider a deployment selects. Outside Development it must be stated and may not be the mock; an HTTP selection
/// needs a base address the adapter can post under and the one secret name it may present. A Development host may run on
/// the unset default, which is the offline adapter, and an unset provider anywhere else refuses the boot.
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
        // The runner applies the overrides first and then removes the inherited keys, so a key this case states itself
        // must stay out of the removal list: a parent environment that already set it would otherwise lose the value.
        var stated = new HashSet<string>(environment.Keys, StringComparer.OrdinalIgnoreCase);
        var inherited = Environment.GetEnvironmentVariables().Keys
            .Cast<string>()
            .Where(key => key.StartsWith("EVIDGATE__", StringComparison.Ordinal))
            .Where(key => !stated.Contains(key))
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
    /// base address it cannot post under — including one carrying user information — or any key secret but its own.
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
    [InlineData("Production", "http", "https://provider.invalid/v1/", SecretNames.SessionPepper, "EVIDGATE__HEALING__LLM__APIKEYSECRET")]
    [InlineData("Production", "http", "https://provider.invalid/v1/", SecretNames.HealingLlmApiKey, null)]
    [InlineData("Production", "http", "https://user:pass@provider.invalid/v1/", SecretNames.HealingLlmApiKey, "EVIDGATE__HEALING__LLM__BASEADDRESS")]
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
        using var services = BootProvider(environment, provider, baseAddress, apiKeySecret);

        if (named is null)
        {
            _ = ProviderOptions(services);
            return;
        }

        ProviderRefusal(services).Message.ShouldContain(named);
    }

    [Fact]
    public void A_Development_Host_With_No_Provider_Runs_The_Offline_Adapter()
    {
        using var services = BootProvider("Development", provider: null);

        ProviderOptions(services).Kind.ShouldBe(HealingProviderKind.Mock);
    }

    [Fact]
    public void A_Production_Host_Stating_None_Runs_No_Adapter()
    {
        using var services = BootProvider("Production", provider: "none");

        ProviderOptions(services).Kind.ShouldBe(HealingProviderKind.None);
    }

    [Fact]
    public void A_Production_Host_With_A_Complete_Http_Selection_Runs_The_Http_Adapter()
    {
        using var services = BootProvider("Production", "http", "https://provider.invalid/v1/", SecretNames.HealingLlmApiKey);

        var selection = ProviderOptions(services);

        selection.Kind.ShouldBe(HealingProviderKind.Http);
        selection.ApiKeySecret.ShouldBe(SecretNames.HealingLlmApiKey);
    }

    /// <summary>The healing module composed for <paramref name="environment"/> with the provider selection a case tests.</summary>
    /// <remarks>
    /// The module is composed on its own: a host's boot check reads the group before it starts and exits, which a test
    /// factory reports as a disposed provider instead of the refusal. The subprocess case above covers the host's exit.
    /// </remarks>
    /// <param name="environment">The host environment the validator reads.</param>
    /// <param name="provider">The <c>Provider</c> value, or null to leave it unset.</param>
    /// <param name="baseAddress">The <c>BaseAddress</c> value, or null to leave it unset.</param>
    /// <param name="apiKeySecret">The <c>ApiKeySecret</c> value, or null to leave it unset.</param>
    private static ServiceProvider BootProvider(
        string environment,
        string? provider,
        string? baseAddress = null,
        string? apiKeySecret = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:Healing:Llm:Provider"] = provider ?? string.Empty,
            ["EVIDGATE:Healing:Llm:BaseAddress"] = baseAddress ?? string.Empty,
            ["EVIDGATE:Healing:Llm:ApiKeySecret"] = apiKeySecret ?? string.Empty,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new ServiceCollection()
            .AddLogging()
            .AddHealingModule(configuration, new NamedEnvironment(environment))
            .BuildServiceProvider();
    }

    /// <summary>The selection the boot judges; reading it runs the validators.</summary>
    /// <param name="provider">The composed module.</param>
    private static HealingProviderOptions ProviderOptions(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<HealingProviderOptions>>().Value;

    /// <summary>The refusal the boot validator makes for <paramref name="provider"/>'s selection.</summary>
    /// <param name="provider">The composed module.</param>
    private static OptionsValidationException ProviderRefusal(IServiceProvider provider)
    {
        void Read() => _ = ProviderOptions(provider);

        return Should.Throw<OptionsValidationException>(Read);
    }

    private sealed class NamedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Evidgate.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
