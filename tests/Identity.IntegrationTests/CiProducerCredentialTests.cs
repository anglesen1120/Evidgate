using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>The signed ingest-token identity, unlike the bearer token id, survives re-exchange.</summary>
[Trait("Category", "Docker")]
public sealed class CiProducerCredentialTests
{
    private static readonly Guid OrgId = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid CredentialId = Guid.Parse("33333333-3333-7333-8333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private const string Issuer = "evidgate-test";
    private const string Audience = "evidgate-test-aud";

    private static readonly byte[] SigningKey = Enumerable.Repeat((byte)7, 32).ToArray();

    [Fact]
    public async Task Reexchange_PreservesCredentialAndProject_ButNotBearerId()
    {
        var tokens = Tokens();
        var first = tokens.Mint(new CiProducerRequest(OrgId, ProjectId, CredentialId, [Permissions.Run.Create]));
        var renewed = tokens.Mint(new CiProducerRequest(OrgId, ProjectId, CredentialId, [Permissions.Run.Create]));

        new JsonWebToken(first.Token).Id.ShouldNotBe(new JsonWebToken(renewed.Token).Id);
        foreach (var token in new[] { first.Token, renewed.Token })
        {
            var caller = await tokens.ValidateAsync(token);
            caller.ShouldNotBeNull();
            caller.Principal.CredentialId.ShouldBe(CredentialId.ToString("D"));
            caller.Principal.ProjectScope.ShouldBe(ProjectId);
        }
    }

    [Fact]
    public async Task DifferentIngestTokenInSameProject_HasItsOwnIdentityAndNoRunClaims()
    {
        var tokens = Tokens();
        var other = Guid.CreateVersion7();
        var minted = tokens.Mint(new CiProducerRequest(OrgId, ProjectId, other, [Permissions.Run.Create]));
        var caller = await tokens.ValidateAsync(minted.Token);

        caller.ShouldNotBeNull();
        caller.Principal.ProjectScope.ShouldBe(ProjectId);
        caller.Principal.CredentialId.ShouldBe(other.ToString("D"));
        var jwt = new JsonWebToken(minted.Token);
        foreach (var name in new[] { "provider", "repository_id", "repository", "ref", "sha", "run_id", "run_attempt", "job_workflow_ref" })
        {
            jwt.TryGetClaim(name, out _).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task MissingOrMalformedCredential_IsNotReplacedByBearerId(string? value)
    {
        var claims = BaseClaims();
        if (value is not null)
        {
            claims["ingest_token_id"] = value;
        }

        (await Tokens().ValidateAsync(MintRaw(claims))).ShouldBeNull();

        claims["ingest_token_id"] = CredentialId.ToString("D");
        (await Tokens().ValidateAsync(MintRaw(claims))).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task MissingOrMalformedProject_IsRefusedDespiteValidCredential(string? value)
    {
        var tokens = Tokens();
        var claims = BaseClaims();
        claims["ingest_token_id"] = CredentialId.ToString("D");
        claims.Remove("project_id");
        if (value is not null)
        {
            claims["project_id"] = value;
        }

        (await tokens.ValidateAsync(MintRaw(claims))).ShouldBeNull();

        claims["project_id"] = ProjectId.ToString("D");
        var caller = await tokens.ValidateAsync(MintRaw(claims));
        caller.ShouldNotBeNull();
        caller.Principal.ProjectScope.ShouldBe(ProjectId);
        caller.Principal.CredentialId.ShouldBe(CredentialId.ToString("D"));
    }

    private static CiProducerTokens Tokens()
    {
        var options = Options.Create(AuthOptions());

        return new CiProducerTokens(options, new CiSecrets(options), new FixedClock(Now));
    }

    private static CiAuthOptions AuthOptions() => new()
    {
        SigningKey = Convert.ToBase64String(SigningKey),
        Issuer = Issuer,
        Audience = Audience,
    };

    private static Dictionary<string, object> BaseClaims() => new(StringComparer.Ordinal)
    {
        ["sub"] = $"ci:{ProjectId:D}",
        ["jti"] = "jti-" + Guid.NewGuid().ToString("N"),
        ["token_use"] = CiProducerTokens.TokenUse,
        ["org_id"] = OrgId.ToString("D"),
        ["project_id"] = ProjectId.ToString("D"),
        ["perm"] = new[] { Permissions.Run.Create },
    };

    /// <summary>
    /// Mints a token with the same key, issuer and audience as <see cref="Tokens"/> but
    /// arbitrary claims, so a case the minter would never write can be presented.
    /// </summary>
    private static string MintRaw(Dictionary<string, object> claims)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = Now.UtcDateTime,
            NotBefore = Now.UtcDateTime,
            Expires = Now.AddMinutes(30).UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(SigningKey), SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler { MapInboundClaims = false }.CreateToken(descriptor);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
