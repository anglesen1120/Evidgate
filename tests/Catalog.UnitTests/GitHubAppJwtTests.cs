using System.Security.Cryptography;
using Evidgate.Modules.Catalog.Infrastructure.GitHub;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

/// <summary>
/// The App JWT: RS256, issuer = App id, <c>iat</c> backdated by a minute, and a total lifetime of at
/// most ten minutes. The clock is the injected <see cref="TimeProvider"/>, so the assertions never
/// depend on the wall clock.
/// </summary>
public sealed class GitHubAppJwtTests
{
    [Fact]
    public async Task AppToken_MintsRs256Jwt_WithIssuerAndTenMinuteLifetime()
    {
        var bench = new GitHubTestBench(new StubHandler(_ => GitHubResponses.Json("{}")));

        var result = await bench.AppTokens.CreateAsync(CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        var jwt = new JsonWebToken(result.Value);
        jwt.Alg.ShouldBe(SecurityAlgorithms.RsaSha256);
        jwt.Issuer.ShouldBe("12345");

        var now = bench.Time.GetUtcNow();
        (jwt.IssuedAt - (now - TimeSpan.FromSeconds(60)).UtcDateTime).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(2));
        (jwt.ValidTo - jwt.IssuedAt).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(10));
        (jwt.ValidTo - now.UtcDateTime).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task AppToken_SignatureValidates_AgainstTheAppPublicKey()
    {
        var bench = new GitHubTestBench(new StubHandler(_ => GitHubResponses.Json("{}")));
        var result = await bench.AppTokens.CreateAsync(CancellationToken.None);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(GitHubTestBench.TestPem);
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = "12345",
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKey = new RsaSecurityKey(rsa.ExportParameters(false)),
            ValidateIssuer = true,
            ValidateAudience = false,
            ValidateLifetime = false,
        };

        var valid = await new JsonWebTokenHandler().ValidateTokenAsync(result.Value, parameters);

        valid.IsValid.ShouldBeTrue();

        // Negative control: the same token with one character changed must not validate.
        var tampered = result.Value[..^2] + (result.Value[^2] == 'A' ? "B" : "A") + result.Value[^1];
        var invalid = await new JsonWebTokenHandler().ValidateTokenAsync(tampered, parameters);
        invalid.IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task AppToken_WithoutAppId_IsNotConfigured_AndSendsNothing()
    {
        var handler = new StubHandler(_ => GitHubResponses.Json("{}"));
        var bench = new GitHubTestBench(handler, GitHubTestBench.DefaultOptions(appId: null));

        var result = await bench.AppTokens.CreateAsync(CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(GitHubErrorCodes.NotConfigured);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AppToken_WithoutSecret_IsNotConfigured()
    {
        var bench = new GitHubTestBench(new StubHandler(_ => GitHubResponses.Json("{}")), pem: string.Empty);

        var result = await bench.AppTokens.CreateAsync(CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(GitHubErrorCodes.NotConfigured);
    }

    [Fact]
    public async Task AppToken_WithUnusablePem_IsNotConfigured_WithoutEchoingTheValue()
    {
        const string NotAKey = "-----BEGIN PRIVATE KEY-----\u0021not-base64\u0021\n-----END PRIVATE KEY-----";
        var bench = new GitHubTestBench(new StubHandler(_ => GitHubResponses.Json("{}")), pem: NotAKey);

        var result = await bench.AppTokens.CreateAsync(CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(GitHubErrorCodes.NotConfigured);
        result.Error.Message.ShouldNotContain("BEGIN", Case.Insensitive);
        result.Error.Message.ShouldNotContain("not-base64", Case.Insensitive);
    }
}
