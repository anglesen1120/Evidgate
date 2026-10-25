using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Google;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Google;

/// <summary>
/// 02.3.1-T1.2 startup contract (CR2, CR3): the configured issuer decides which <c>iss</c> spellings are
/// accepted instead of a hard-coded pair, and a blank deployment setting (null, empty or whitespace) means
/// ABSENT: the provider reports itself unconfigured and the routes answer 503 instead of the host failing
/// at boot on a value the deploy tool injected empty.
/// </summary>
/// <remarks>
/// Pure: the validator and the option validators are exercised directly, and the composition test only
/// builds the service provider (no request leaves the process).
/// </remarks>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class GoogleIssuerAndAbsentSettingsTests
{
    // -----------------------------------------------------------------------------------------------
    // CR2: the configured issuer, plus the other spelling of the same host.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task GoogleValidator_ConfiguredHttpsIssuer_AcceptsBothSpellings()
    {
        var harness = new GoogleValidatorHarness(Settings(GoogleEndpoints.Issuer));
        var cancellationToken = TestContext.Current.CancellationToken;

        var configured = harness.Tokens.Mint();
        (await harness.Validator.ValidateAsync(configured, GoogleTestHarness.Nonce, cancellationToken))
            .Identity.ShouldNotBeNull("the token carries the configured issuer");

        var bare = harness.Tokens.Mint(signing => signing.Issuer = GoogleEndpoints.IssuerAlias);
        (await harness.Validator.ValidateAsync(bare, GoogleTestHarness.Nonce, cancellationToken))
            .Identity.ShouldNotBeNull("Google still publishes the bare-host spelling of the same issuer");
    }

    [Fact]
    public async Task GoogleValidator_ConfiguredBareIssuer_AcceptsBothSpellings()
    {
        var harness = new GoogleValidatorHarness(Settings(GoogleEndpoints.IssuerAlias));
        var cancellationToken = TestContext.Current.CancellationToken;

        var configured = harness.Tokens.Mint(signing => signing.Issuer = GoogleEndpoints.IssuerAlias);
        (await harness.Validator.ValidateAsync(configured, GoogleTestHarness.Nonce, cancellationToken))
            .Identity.ShouldNotBeNull("the token carries the configured bare-host issuer");

        var https = harness.Tokens.Mint(signing => signing.Issuer = GoogleEndpoints.Issuer);
        (await harness.Validator.ValidateAsync(https, GoogleTestHarness.Nonce, cancellationToken))
            .Identity.ShouldNotBeNull("the other spelling of the configured issuer is the same issuer");
    }

    [Fact]
    public async Task GoogleValidator_ConfiguredIssuer_StillRefusesAnotherHost()
    {
        var harness = new GoogleValidatorHarness(Settings(GoogleEndpoints.Issuer));

        var foreign = harness.Tokens.Mint(signing => signing.Issuer = "https://accounts.evidgate-attacker.test");

        var result = await harness.Validator.ValidateAsync(foreign, GoogleTestHarness.Nonce, TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.TokenInvalid, "deriving the spelling from the configured issuer must not widen it to any host");
    }

    // -----------------------------------------------------------------------------------------------
    // CR3: blank is absent, for the browser-facing base URL and for every Google setting.
    // -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void LoginOptionsValidator_TreatsABlankPublicBaseUrlAsAbsent(string value)
    {
        var options = new LoginOptions { PublicBaseUrl = value };

        new LoginOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue(
            "an empty injected value is absent, not a misconfiguration that fails the host at boot");
        options.CallbackUri("google").ShouldBeNull(
            "without a base URL the routes answer 503 rather than build a redirect URI the provider does not know");
    }

    [Fact]
    public void GoogleOptionsValidator_TreatsABlankClientIdAsAbsent() =>
        AssertAbsent(new GoogleLoginOptions { ClientId = " " });

    [Fact]
    public void GoogleOptionsValidator_TreatsABlankClientSecretAsAbsent() =>
        AssertAbsent(new GoogleLoginOptions { ClientSecret = string.Empty });

    [Fact]
    public void GoogleOptionsValidator_TreatsABlankIssuerAsAbsent() =>
        AssertAbsent(new GoogleLoginOptions { Issuer = "  " });

    [Fact]
    public void GoogleOptionsValidator_TreatsABlankAuthorizationEndpointAsAbsent() =>
        AssertAbsent(new GoogleLoginOptions { AuthorizationEndpoint = string.Empty });

    [Fact]
    public void GoogleOptionsValidator_TreatsABlankTokenEndpointAsAbsent() =>
        AssertAbsent(new GoogleLoginOptions { TokenEndpoint = " " });

    [Fact]
    public void GoogleOptionsValidator_TreatsABlankJwksUriAsAbsent() =>
        AssertAbsent(new GoogleLoginOptions { JwksUri = string.Empty });

    [Fact]
    public async Task GoogleLoginComposition_WithBlankSettings_BootsAndAnswersUnavailable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{GoogleLoginOptions.SectionName}:ClientId"] = string.Empty,
                [$"{GoogleLoginOptions.SectionName}:Issuer"] = "   ",
                [$"{GoogleLoginOptions.SectionName}:AuthorizationEndpoint"] = string.Empty,
                [$"{GoogleLoginOptions.SectionName}:TokenEndpoint"] = " ",
                [$"{GoogleLoginOptions.SectionName}:JwksUri"] = string.Empty,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretProvider>(new GoogleTestSecretProvider(null));
        services.AddGoogleLogin(configuration);

        using var provider = services.BuildServiceProvider();

        // Resolving the options runs exactly the validators the host runs at boot (ValidateOnStart): a
        // blank setting that still failed validation would throw OptionsValidationException right here,
        // which fails this test with that message.
        var settings = provider.GetRequiredService<IOptions<GoogleLoginOptions>>().Value;
        settings.ClientId.ShouldBeNullOrEmpty();

        var login = provider.GetRequiredService<ILoginProvider>();
        login.IsConfigured.ShouldBeFalse("a blank credential or endpoint leaves the provider unconfigured");

        var result = await login.CompleteAsync(Callback(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable, "AC8: 503 unavailable, never a boot failure and never a 500");
    }

    private static void AssertAbsent(GoogleLoginOptions options) =>
        new GoogleLoginOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue(
            "a blank deployment setting is absent: the provider answers 503 instead of failing the host at boot");

    private static GoogleLoginOptions Settings(string issuer) => new()
    {
        ClientId = GoogleTestHarness.ClientId,
        ClientSecret = GoogleTestHarness.ClientSecret,
        Issuer = issuer,
    };

    private static LoginCallbackRequest Callback() => new(
        "test-authorization-code",
        "test-code-verifier-0123456789abcdef",
        GoogleTestHarness.Nonce,
        GoogleTestHarness.RedirectUri);
}
