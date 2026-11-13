using System.Security.Cryptography;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
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
    public void AppToken_MintsRs256Jwt_WithIssuerAndTenMinuteLifetime()
    {
        var bench = new GitHubTestBench(new StubHandler(_ => GitHubResponses.Json("{}")));

        var result = bench.AppTokens.Create();

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
        var result = bench.AppTokens.Create();

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
    public void AppToken_WithoutAppId_IsNotConfigured_AndSendsNothing()
    {
        var handler = new StubHandler(_ => GitHubResponses.Json("{}"));
        var bench = new GitHubTestBench(handler, GitHubTestBench.DefaultOptions(appId: null));

        var result = bench.AppTokens.Create();

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(GitHubErrorCodes.NotConfigured);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void AppToken_WithoutSecret_IsNotConfigured()
    {
        var bench = new GitHubTestBench(new StubHandler(_ => GitHubResponses.Json("{}")), pem: string.Empty);

        var result = bench.AppTokens.Create();

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(GitHubErrorCodes.NotConfigured);
    }

    [Fact]
    public void AppToken_WithAPublicOnlyPem_IsNotConfigured()
    {
        using var rsa = RSA.Create(2048);
        var bench = new GitHubTestBench(new StubHandler(_ => GitHubResponses.Json("{}")), pem: rsa.ExportSubjectPublicKeyInfoPem());

        var result = bench.AppTokens.Create();

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(GitHubErrorCodes.NotConfigured);
        bench.AppTokenLogs.Lines.ShouldContain(line => line.Contains("CryptographicException", StringComparison.Ordinal));
    }

    [Fact]
    public void AppToken_WithATruncatedPem_IsNotConfigured()
    {
        var bench = new GitHubTestBench(new StubHandler(_ => GitHubResponses.Json("{}")), pem: GitHubTestBench.TestPem[..40]);

        var result = bench.AppTokens.Create();

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(GitHubErrorCodes.NotConfigured);
    }

    [Fact]
    public void AppToken_WithANonPrivatePemBody_IsNotConfigured_WithoutEchoingTheValue()
    {
        using var rsa = RSA.Create(2048);
        var lines = rsa.ExportPkcs8PrivateKeyPem().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var damaged = $"{lines[0]}\nnot-base64\n{lines[^1]}\n";
        var bench = new GitHubTestBench(new StubHandler(_ => GitHubResponses.Json("{}")), pem: damaged);

        var result = bench.AppTokens.Create();

        result.IsSuccess.ShouldBeFalse(result.Error?.Message);
        result.Error!.Code.ShouldBe(GitHubErrorCodes.NotConfigured);
        result.Error.Message.ShouldNotContain("not-base64", Case.Insensitive);
        bench.AppTokenLogs.Lines.Count.ShouldBe(1);
        foreach (var line in bench.AppTokenLogs.Lines)
        {
            line.ShouldNotContain("BEGIN", Case.Insensitive);
            line.ShouldNotContain(lines[1], Case.Sensitive);
        }
    }

    [Fact]
    public void AppToken_WithAnUnusableKey_ReportsTheFailureOncePerKey_WithoutKeyMaterial()
    {
        using var rsa = RSA.Create(2048);
        var bench = new GitHubTestBench(new StubHandler(_ => GitHubResponses.Json("{}")), pem: rsa.ExportSubjectPublicKeyInfoPem());

        bench.AppTokens.Create();
        bench.AppTokens.Create();
        bench.AppTokens.Create();

        bench.AppTokenLogs.Lines.Count(line => line.Contains("cannot sign", StringComparison.Ordinal)).ShouldBe(1);
        bench.AppTokenLogs.Lines.Count.ShouldBe(1);
        var keyBody = rsa.ExportSubjectPublicKeyInfoPem().Split('\n', StringSplitOptions.RemoveEmptyEntries)[1];
        foreach (var line in bench.AppTokenLogs.Lines)
        {
            line.ShouldNotContain("BEGIN", Case.Insensitive);
            line.ShouldNotContain(keyBody, Case.Sensitive);
        }
    }
}
