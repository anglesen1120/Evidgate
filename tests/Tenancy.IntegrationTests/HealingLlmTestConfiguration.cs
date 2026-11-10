using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The configuration and host environment a Healing composition needs in a test, in one place: the provider selection is
/// deployment configuration and the module reads the host environment to decide what an unset selection means.
/// </summary>
internal static class HealingLlmTestConfiguration
{
    /// <summary>The <c>EVIDGATE:Healing:Llm</c> section for <paramref name="provider"/>.</summary>
    /// <param name="provider">The provider word: <c>none</c>, <c>mock</c> or <c>http</c>.</param>
    /// <param name="baseAddress">The HTTP provider's base address, when the case composes the HTTP kind.</param>
    /// <param name="apiKeySecret">The secret <em>name</em> of the HTTP provider's key, when the case composes that kind.</param>
    /// <param name="applyEnabled">Whether the case turns the V1 clamp on (<c>EVIDGATE:Healing:Apply</c>).</param>
    internal static IConfiguration Build(
        string provider,
        string? baseAddress = null,
        string? apiKeySecret = null,
        bool applyEnabled = false)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:Healing:Llm:Provider"] = provider,
        };

        if (applyEnabled)
        {
            values["EVIDGATE:Healing:Apply"] = "true";
        }

        if (baseAddress is not null)
        {
            values["EVIDGATE:Healing:Llm:BaseAddress"] = baseAddress;
        }

        if (apiKeySecret is not null)
        {
            values["EVIDGATE:Healing:Llm:ApiKeySecret"] = apiKeySecret;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>The smallest host environment the provider selection reads: its name.</summary>
    /// <param name="environmentName">One of the <see cref="Environments"/> names.</param>
    internal static IHostEnvironment Host(string environmentName) =>
        new TestEnvironment { EnvironmentName = environmentName };

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Evidgate.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
