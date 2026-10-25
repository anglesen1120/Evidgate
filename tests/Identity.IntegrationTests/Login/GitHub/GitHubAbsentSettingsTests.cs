using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.GitHub;

/// <summary>
/// 02.3.1-T1.3 startup contract: a blank deployment setting (null, empty or whitespace) means ABSENT — the
/// provider reports itself unconfigured and the routes answer 503 instead of the host failing at boot on a
/// value the deploy tool injected empty — and the client secret is written by the post-configurer only,
/// through <see cref="ISecretProvider"/>, never by the binder (10.1.1-T2).
/// </summary>
/// <remarks>
/// Pure: the validators are exercised directly and the composition tests only build the service provider
/// (no request leaves the process).
/// </remarks>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class GitHubAbsentSettingsTests
{
    [Fact]
    public void GitHubOptionsValidator_TreatsABlankClientIdAsAbsent() =>
        AssertAbsent(GitHubTestHarness.Options(clientId: " "));

    [Fact]
    public void GitHubOptionsValidator_TreatsABlankClientSecretAsAbsent() =>
        AssertAbsent(GitHubTestHarness.Options(clientSecret: string.Empty));

    [Fact]
    public void GitHubOptionsValidator_TreatsABlankAuthorizeUrlAsAbsent() =>
        AssertAbsent(GitHubTestHarness.Options(authorizeUrl: "  "));

    [Fact]
    public void GitHubOptionsValidator_TreatsABlankTokenUrlAsAbsent() =>
        AssertAbsent(GitHubTestHarness.Options(tokenUrl: string.Empty));

    [Fact]
    public void GitHubOptionsValidator_TreatsABlankApiBaseUrlAsAbsent() =>
        AssertAbsent(GitHubTestHarness.Options(apiBaseUrl: " "));

    [Fact]
    public void GitHubProvider_WithBlankSettings_ReportsItselfUnconfigured()
    {
        var harness = new GitHubProviderHarness(settings: new GitHubLoginOptions
        {
            ClientId = " ",
            ClientSecret = string.Empty,
            AuthorizeUrl = "  ",
            TokenUrl = string.Empty,
            ApiBaseUrl = " ",
        });

        harness.Provider.IsConfigured.ShouldBeFalse("a blank endpoint would make the URL built from it invalid");
    }

    [Fact]
    public async Task GitHubLoginComposition_WithBlankSettings_BootsAndAnswersUnavailable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{GitHubLoginOptions.SectionName}:ClientId"] = string.Empty,
                [$"{GitHubLoginOptions.SectionName}:AuthorizeUrl"] = "   ",
                [$"{GitHubLoginOptions.SectionName}:TokenUrl"] = string.Empty,
                [$"{GitHubLoginOptions.SectionName}:ApiBaseUrl"] = " ",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProvider>(new GitHubTestSecretProvider(null));
        services.AddGitHubLogin(configuration);

        using var provider = services.BuildServiceProvider();

        // Resolving the options runs exactly the validators the host runs at boot (ValidateOnStart): a
        // blank setting that still failed validation would throw OptionsValidationException right here,
        // which fails this test with that message.
        var settings = provider.GetRequiredService<IOptions<GitHubLoginOptions>>().Value;
        settings.ClientId.ShouldBeNullOrEmpty();

        var login = provider.GetRequiredService<ILoginProvider>();
        login.Name.ShouldBe("github");
        login.IsConfigured.ShouldBeFalse("a blank credential or endpoint leaves the provider unconfigured");

        var result = await login.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable, "AC8: 503 unavailable, never a boot failure and never a 500");
    }

    [Fact]
    public void GitHubLoginComposition_ResolvesTheClientSecretThroughTheSecretProvider()
    {
        // A value in configuration must be ignored: the binder skips the internal setter, so the
        // post-configurer is the only writer of ClientSecret.
        const string FromConfiguration = "configuration-value-must-be-ignored";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{GitHubLoginOptions.SectionName}:ClientId"] = GitHubTestHarness.ClientId,
                [$"{GitHubLoginOptions.SectionName}:ClientSecret"] = FromConfiguration,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProvider>(new GitHubTestSecretProvider(GitHubTestHarness.ClientSecret));
        services.AddGitHubLogin(configuration);

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<GitHubLoginOptions>>().Value;

        settings.ClientSecret.ShouldBe(GitHubTestHarness.ClientSecret);
        settings.ClientSecret.ShouldNotBe(FromConfiguration, "a bound secret would travel through configuration sources");
        provider.GetRequiredService<ILoginProvider>().IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public void GitHubLoginComposition_WithoutASecretLeavesTheProviderUnconfigured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{GitHubLoginOptions.SectionName}:ClientId"] = GitHubTestHarness.ClientId,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProvider>(new GitHubTestSecretProvider(null));
        services.AddGitHubLogin(configuration);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<GitHubLoginOptions>>().Value.ClientSecret.ShouldBeNull();
        provider.GetRequiredService<ILoginProvider>().IsConfigured.ShouldBeFalse();
    }

    private static void AssertAbsent(GitHubLoginOptions options) =>
        new GitHubLoginOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue(
            "a blank deployment setting is absent: the provider answers 503 instead of failing the host at boot");
}
