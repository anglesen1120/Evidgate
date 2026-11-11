using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Healing;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// Which adapter a composed container can resolve. The mock's registration exists only in Development, so a production
/// container has nothing to resolve even when the configuration asks for it, and an unselected provider runs the disabled
/// adapter rather than reaching a network.
/// </summary>
public sealed class HealingProviderSelectionTests
{
    [Fact]
    public void A_Production_Composition_Cannot_Resolve_The_Mock_Even_When_It_Is_Configured()
    {
        var services = Compose(Environments.Production, provider: "mock");

        services
            .Any(descriptor => descriptor.ServiceType == typeof(IHealingLlmProvider) && ImplementsMock(descriptor))
            .ShouldBeFalse("the mock is registered only inside the Development branch");

        using var provider = services.BuildServiceProvider();

        // Fail closed: the selection is refused before any adapter is chosen, and the message names the key.
        var refusal = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IHealingLlmProvider>());

        refusal.Message.ShouldContain(HealingProviderKeys.Provider);
        refusal.Message.ShouldNotContain(nameof(MockHealingLlmProvider));
    }

    [Fact]
    public void A_Development_Composition_With_No_Provider_Resolves_The_Offline_Adapter()
    {
        var services = Compose(Environments.Development);

        services
            .Any(descriptor => descriptor.ServiceType == typeof(IHealingLlmProvider) && ImplementsMock(descriptor))
            .ShouldBeTrue("Development is the only environment the mock is registered in");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IHealingLlmProvider>().ShouldBeOfType<MockHealingLlmProvider>();
        provider.GetRequiredService<IOptions<HealingProviderOptions>>().Value.Kind.ShouldBe(HealingProviderKind.Mock);
    }

    [Fact]
    public void A_Production_Composition_Stating_None_Resolves_The_Disabled_Adapter()
    {
        using var provider = Compose(Environments.Production, provider: "none").BuildServiceProvider();

        provider.GetRequiredService<IHealingLlmProvider>().ShouldBeOfType<DisabledHealingLlmProvider>();
        provider.GetRequiredService<IOptions<HealingProviderOptions>>().Value.Kind.ShouldBe(HealingProviderKind.None);
    }

    [Fact]
    public void A_Production_Composition_With_A_Complete_Http_Selection_Resolves_The_Http_Adapter()
    {
        var services = Compose(
            Environments.Production,
            provider: "http",
            baseAddress: "https://provider.invalid/v1/",
            apiKeySecret: SecretNames.HealingLlmApiKey);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IHealingLlmProvider>().ShouldBeOfType<HttpHealingLlmProvider>();
    }

    /// <summary>
    /// Whether a descriptor would build the mock. The mock is registered keyed, and a keyed descriptor's
    /// <see cref="ServiceDescriptor.ImplementationType"/> is null (reading it throws on the keyed property), so the kind is
    /// read from whichever member the registration actually set.
    /// </summary>
    /// <param name="descriptor">A registration of <see cref="IHealingLlmProvider"/>.</param>
    private static bool ImplementsMock(ServiceDescriptor descriptor) =>
        (descriptor.IsKeyedService ? descriptor.KeyedImplementationType : descriptor.ImplementationType)
        == typeof(MockHealingLlmProvider);

    /// <summary>A module composition over in-memory configuration and an explicit host environment.</summary>
    /// <param name="environmentName">The environment the selection is judged in.</param>
    /// <param name="provider">The <c>Provider</c> value, or null to leave it unset.</param>
    /// <param name="baseAddress">The <c>BaseAddress</c> value, or null to leave it unset.</param>
    /// <param name="apiKeySecret">The <c>ApiKeySecret</c> value, or null to leave it unset.</param>
    private static ServiceCollection Compose(
        string environmentName,
        string? provider = null,
        string? baseAddress = null,
        string? apiKeySecret = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (provider is not null)
        {
            settings["EVIDGATE:Healing:Llm:Provider"] = provider;
        }

        if (baseAddress is not null)
        {
            settings["EVIDGATE:Healing:Llm:BaseAddress"] = baseAddress;
        }

        if (apiKeySecret is not null)
        {
            settings["EVIDGATE:Healing:Llm:ApiKeySecret"] = apiKeySecret;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var environment = new TestHostEnvironment(environmentName);

        var services = new ServiceCollection();
        services.AddLogging();

        // The module's provider post-configuration and its validator read the host environment from the container.
        services.AddSingleton<IHostEnvironment>(environment);

        // The HTTP adapter's request handler resolves its key through this seam; no case here sends a request.
        services.AddSingleton<ISecretProvider, StubSecretProvider>();
        services.AddHealingModule(configuration, environment);

        return services;
    }

    /// <summary>A secret provider that answers every name, so resolving the HTTP adapter needs no environment.</summary>
    private sealed class StubSecretProvider : ISecretProvider
    {
        public ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>("test-key");
    }

    /// <summary>The smallest <see cref="IHostEnvironment"/> the selection reads: its name.</summary>
    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Evidgate.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
