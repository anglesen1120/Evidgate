using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Catalog.Infrastructure.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

/// <summary>
/// The module's one composition entry: binding the App options and resolving the client through
/// <c>IHttpClientFactory</c> must work from a plain service collection, and the options must carry
/// what the host bound.
/// </summary>
public sealed class GitHubCompositionTests
{
    [Fact]
    public void AddCatalogGitHub_ResolvesTheClientAndBindsTheOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{GitHubOptions.SectionName}:AppId"] = "12345",
                [$"{GitHubOptions.SectionName}:MaxTreeEntries"] = "10",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProvider>(new MapSecretProvider(GitHubTestBench.TestPem));
        services.AddCatalogGitHub(configuration);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<GitHubClient>().ShouldNotBeNull();
        var options = provider.GetRequiredService<IOptions<GitHubOptions>>().Value;
        options.AppId.ShouldBe("12345");
        options.MaxTreeEntries.ShouldBe(10);
        new GitHubOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }
}
