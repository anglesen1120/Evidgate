using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.Modules.Catalog.Infrastructure.GitHub;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

/// <summary>
/// Boot validation of the App options: the API host is pinned (S4), the App id is the numeric id
/// GitHub issued, and the private-key reference is a registered secret name. A message names the
/// key only, never the value it refused.
/// </summary>
public sealed class GitHubOptionsValidatorTests
{
    [Fact]
    public void Validator_AcceptsThePinnedDefaults()
    {
        Validate(new GitHubOptions { AppId = "12345" }).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validator_AcceptsAnAbsentAppId()
    {
        Validate(new GitHubOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validator_RefusesANonGithubHost()
    {
        var result = Validate(new GitHubOptions { AppId = "12345", ApiBaseUrl = "https://evil.example.com" });

        result.Failed.ShouldBeTrue();
        result.Failures!.Single().ShouldContain(nameof(GitHubOptions.ApiBaseUrl));
        result.Failures!.Single().ShouldNotContain("evil.example.com");
    }

    [Fact]
    public void Validator_RefusesAnHttpBaseAddress()
    {
        Validate(new GitHubOptions { AppId = "12345", ApiBaseUrl = "http://api.github.com" }).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Validator_RefusesABaseAddressWithAPath()
    {
        Validate(new GitHubOptions { AppId = "12345", ApiBaseUrl = "https://api.github.com/v3" }).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Validator_RefusesANonNumericAppId()
    {
        var result = Validate(new GitHubOptions { AppId = "12x3" });

        result.Failed.ShouldBeTrue();
        result.Failures!.Single().ShouldContain(nameof(GitHubOptions.AppId));
        result.Failures!.Single().ShouldNotContain("12x3");
    }


    [Fact]
    public void Validator_RefusesASafetyMarginAtHalfTheTokenLifetime()
    {
        Validate(new GitHubOptions { AppId = "12345", InstallationTokenSafetyMarginSeconds = 1800 }).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Validator_AcceptsAnOAuthClientIdWithDots()
    {
        Validate(new GitHubOptions { AppId = "12345", ClientId = "Iv1.0123456789abcdef" }).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validator_RefusesAClientIdWithAPathSeparator()
    {
        var result = Validate(new GitHubOptions { AppId = "12345", ClientId = "Iv1/0123" });

        result.Failed.ShouldBeTrue();
        result.Failures!.Single().ShouldContain(nameof(GitHubOptions.ClientId));
        result.Failures!.Single().ShouldNotContain("Iv1/0123");
    }

    [Fact]
    public void Validator_RefusesAnOversizedClientId()
    {
        Validate(new GitHubOptions { AppId = "12345", ClientId = new string('a', 65) }).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Validator_AcceptsASlugOfLettersDigitsAndSeparators()
    {
        Validate(new GitHubOptions { AppId = "12345", AppSlug = "evidgate-app_1" }).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validator_RefusesASlugWithADot()
    {
        var result = Validate(new GitHubOptions { AppId = "12345", AppSlug = "evidgate.app" });

        result.Failed.ShouldBeTrue();
        result.Failures!.Single().ShouldContain(nameof(GitHubOptions.AppSlug));
    }

    private static ValidateOptionsResult Validate(GitHubOptions options) =>
        new GitHubOptionsValidator().Validate(null, options);
}
