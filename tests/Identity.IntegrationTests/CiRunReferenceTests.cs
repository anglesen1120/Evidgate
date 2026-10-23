using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// T88 / 05.2.1-T1 slice B: a minted <c>ci_producer</c> token validates to a principal
/// whose <see cref="IRequestPrincipal.CiRun"/> is exactly the four claims the exchange
/// copied from the verified GitHub OIDC token (<c>provider</c>, <c>repository_id</c>,
/// <c>run_id</c>, <c>run_attempt</c>), and to null for an ingest token or any token that
/// is missing or garbling one of them. Pure: no database, no host.
/// </summary>
/// <remarks>
/// The tokens are minted by the module's own <see cref="CiProducerTokens"/> over a fixed
/// key, so the assertion is about the shipping mint/validate pair. The missing/garbled
/// cases are built with the same key but arbitrary claims, because the minter only ever
/// writes a well-formed run.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class CiRunReferenceTests
{
    private static readonly Guid OrgId = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid BindingId = Guid.Parse("33333333-3333-7333-8333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private const string Issuer = "evidgate-test";
    private const string Audience = "evidgate-test-aud";

    private static readonly byte[] SigningKey = Enumerable.Repeat((byte)7, 32).ToArray();

    /// <summary>A minted <c>github_actions</c> token validates to the exact run claims it was minted with.</summary>
    [Fact]
    public async Task GithubActions_Token_Validates_WithTheMintedCiRun()
    {
        var tokens = Tokens();
        var run = new CiRunClaims(
            RepositoryId: 987_654_321L,
            Repository: "acme/web",
            Ref: "refs/heads/main",
            Sha: "0123456789abcdef0123456789abcdef01234567",
            RunId: 42_424_242L,
            RunAttempt: 3,
            JobWorkflowRef: "acme/web/.github/workflows/ci.yml@refs/heads/main");

        var caller = await tokens.ValidateAsync(Mint(tokens, "github_actions", run));

        caller.ShouldNotBeNull();
        caller.Principal.CiRun.ShouldBe(new CiRunReference("github_actions", 987_654_321L, 42_424_242L, 3));
    }

    /// <summary>An ingest-token credential carries no run: its principal's <c>CiRun</c> is null.</summary>
    [Fact]
    public async Task IngestToken_Token_Validates_WithNoCiRun()
    {
        var tokens = Tokens();

        var caller = await tokens.ValidateAsync(Mint(tokens, "ingest_token", run: null));

        caller.ShouldNotBeNull();
        caller.Principal.CiRun.ShouldBeNull();
    }

    /// <summary>A <c>github_actions</c> token minted without run claims (the exchange had none) has no <c>CiRun</c>.</summary>
    [Fact]
    public async Task GithubActions_Token_WithoutRunClaims_Validates_WithNoCiRun()
    {
        var tokens = Tokens();

        var caller = await tokens.ValidateAsync(Mint(tokens, "github_actions", run: null));

        caller.ShouldNotBeNull();
        caller.Principal.CiRun.ShouldBeNull();
    }

    /// <summary>
    /// The ids are read tolerantly: GitHub sends them as JSON strings, so a token whose
    /// four claims are strings still yields the run.
    /// </summary>
    [Fact]
    public async Task GithubActions_Token_WithStringIds_Validates_WithTheCiRun()
    {
        var claims = BaseClaims("github_actions");
        claims["repository_id"] = "987654321";
        claims["run_id"] = "42424242";
        claims["run_attempt"] = "3";

        var caller = await Tokens().ValidateAsync(MintRaw(claims));

        caller.ShouldNotBeNull();
        caller.Principal.CiRun.ShouldBe(new CiRunReference("github_actions", 987_654_321L, 42_424_242L, 3));
    }

    /// <summary>
    /// A token whose provider is not <c>github_actions</c>, or that is missing or garbling
    /// any of the four claims, still authenticates but carries no run.
    /// </summary>
    [Theory]
    [InlineData("provider", "ingest_token")]
    [InlineData("provider", "gitlab")]
    [InlineData("repository_id", null)]
    [InlineData("repository_id", "not-a-number")]
    [InlineData("repository_id", 0L)]
    [InlineData("run_id", null)]
    [InlineData("run_id", "abc")]
    [InlineData("run_id", 0L)]
    [InlineData("run_attempt", null)]
    [InlineData("run_attempt", "abc")]
    [InlineData("run_attempt", 0)]
    public async Task GithubActions_Token_MissingOrGarbledClaim_Validates_WithNoCiRun(string claim, object? value)
    {
        var claims = BaseClaims("github_actions");
        claims["repository_id"] = 987_654_321L;
        claims["run_id"] = 42_424_242L;
        claims["run_attempt"] = 3;

        if (value is null)
        {
            claims.Remove(claim);
        }
        else
        {
            claims[claim] = value;
        }

        var caller = await Tokens().ValidateAsync(MintRaw(claims));

        caller.ShouldNotBeNull();
        caller.Principal.CiRun.ShouldBeNull();
    }

    /// <summary>Only a service credential can carry a run; the other principal kinds and a run-less service token are null.</summary>
    [Fact]
    public void Member_Operator_And_RunlessService_Principals_HaveNoCiRun()
    {
        RequestPrincipal.Member(Guid.NewGuid()).CiRun.ShouldBeNull();
        RequestPrincipal.Operator().CiRun.ShouldBeNull();
        RequestPrincipal.Service(ProjectId, [Permissions.Run.Create]).CiRun.ShouldBeNull();
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

    private static string Mint(CiProducerTokens tokens, string provider, CiRunClaims? run) =>
        tokens.Mint(new CiProducerRequest(
            OrgId,
            ProjectId,
            BindingId,
            provider,
            [Permissions.Run.Create],
            "jti-" + Guid.NewGuid().ToString("N"),
            run)).Token;

    /// <summary>The claims every minted token carries, so a raw token still passes the module's validation.</summary>
    private static Dictionary<string, object> BaseClaims(string provider) => new(StringComparer.Ordinal)
    {
        ["sub"] = $"ci:{ProjectId:D}",
        ["jti"] = "jti-" + Guid.NewGuid().ToString("N"),
        ["token_use"] = CiProducerTokens.TokenUse,
        ["org_id"] = OrgId.ToString("D"),
        ["project_id"] = ProjectId.ToString("D"),
        ["binding_id"] = BindingId.ToString("D"),
        ["provider"] = provider,
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
