using System.Security.Claims;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Identity.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Evidgate.Modules.Identity.Ci;

/// <summary>What an exchange asks the minter to put into a <c>ci_producer</c> token (AC6, T88).</summary>
/// <param name="OrgId">The tenant.</param>
/// <param name="ProjectId">The one project the token is scoped to.</param>
/// <param name="BindingId">The binding or ingest token that authorised it.</param>
/// <param name="Provider"><c>github_actions</c> or <c>ingest_token</c>.</param>
/// <param name="Permissions">The permission set, a subset of the service ceiling.</param>
/// <param name="Jti">A fresh unique id.</param>
/// <param name="Run">The CI run claims of a GitHub exchange, or null for an ingest token.</param>
internal sealed record CiProducerRequest(
    Guid OrgId,
    Guid ProjectId,
    Guid BindingId,
    string Provider,
    IReadOnlyList<string> Permissions,
    string Jti,
    CiRunClaims? Run);

/// <summary>The T88 claims copied from the verified GitHub token into the minted one.</summary>
internal sealed record CiRunClaims(
    long RepositoryId,
    string Repository,
    string Ref,
    string Sha,
    long RunId,
    int RunAttempt,
    string JobWorkflowRef);

/// <summary>A minted token and when it stops being valid.</summary>
/// <param name="Token">The compact JWT.</param>
/// <param name="ExpiresAt">Its <c>exp</c>.</param>
internal sealed record MintedCiToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Mints and validates the platform's own <c>ci_producer</c> JWT (D7): HS256 over the configured
/// signing key, our issuer and audience, at most 60 minutes of life, never refreshed (a job that
/// outlives it exchanges again with a fresh OIDC token).
/// </summary>
internal sealed class CiProducerTokens(IOptions<CiAuthOptions> options, CiSecrets secrets, TimeProvider clock)
{
    internal const string TokenUse = "ci_producer";

    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    private readonly CiAuthOptions _options = options.Value;

    /// <summary>Whether a signing key is configured (otherwise the exchange is off).</summary>
    public bool Enabled => secrets.SigningKey is not null;

    /// <summary>Mints the token. The permission set is intersected with the service ceiling, so a caller cannot widen it.</summary>
    /// <param name="request">What to put into it.</param>
    public MintedCiToken Mint(CiProducerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = secrets.SigningKey ?? throw new InvalidOperationException("no signing key is configured");
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(_options.TokenLifetimeMinutes);

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = $"ci:{request.ProjectId:D}",
            ["jti"] = request.Jti,
            ["token_use"] = TokenUse,
            ["org_id"] = request.OrgId.ToString("D"),
            ["project_id"] = request.ProjectId.ToString("D"),
            ["binding_id"] = request.BindingId.ToString("D"),
            ["provider"] = request.Provider,
            ["perm"] = request.Permissions.ToArray(),
        };

        if (request.Run is { } run)
        {
            claims["repository_id"] = run.RepositoryId;
            claims["repository"] = run.Repository;
            claims["ref"] = run.Ref;
            claims["sha"] = run.Sha;
            claims["run_id"] = run.RunId;
            claims["run_attempt"] = run.RunAttempt;
            claims["job_workflow_ref"] = run.JobWorkflowRef;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256),
        };

        return new MintedCiToken(Handler.CreateToken(descriptor), expires);
    }

    /// <summary>
    /// Validates a presented token as one of ours. Returns the caller, or null for anything else
    /// (wrong algorithm, issuer, audience, expired, not a <c>ci_producer</c>, claims outside the service ceiling).
    /// </summary>
    /// <param name="token">The compact JWT.</param>
    public async ValueTask<AuthenticatedCaller?> ValidateAsync(string token)
    {
        if (secrets.SigningKey is not { } key || string.IsNullOrEmpty(token) || token.Length > 4096)
        {
            return null;
        }

        var result = await Handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = _options.Issuer,
            ValidAudience = _options.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            LifetimeValidator = (notBefore, expires, _, parameters) =>
                LifetimeCheck.IsWithin(clock.GetUtcNow(), notBefore, expires, parameters.ClockSkew),
        }).ConfigureAwait(false);

        if (!result.IsValid)
        {
            return null;
        }

        if (result.SecurityToken is not JsonWebToken jwt)
        {
            return null;
        }

        if (!TryGetString(jwt, "token_use", out var use) || use != TokenUse
            || !TryGetGuid(jwt, "org_id", out var org)
            || !TryGetGuid(jwt, "project_id", out var project)
            || !TryGetString(jwt, "jti", out var jti)
            || !jwt.TryGetPayloadValue<string[]>("perm", out var permissions)
            || permissions.Any(permission => !PermissionEvaluatorCeiling.Contains(permission)))
        {
            return null;
        }

        return new AuthenticatedCaller(RequestPrincipal.Service(project, permissions, jti), org, project);
    }

    private static bool TryGetString(JsonWebToken jwt, string name, out string value)
    {
        value = string.Empty;

        if (!jwt.TryGetPayloadValue<string>(name, out var found) || string.IsNullOrEmpty(found))
        {
            return false;
        }

        value = found;

        return true;
    }

    private static bool TryGetGuid(JsonWebToken jwt, string name, out Guid value)
    {
        value = default;

        return TryGetString(jwt, name, out var text) && Guid.TryParse(text, out value);
    }
}

/// <summary>The permissions a service token may carry: the union of the catalogue's service roles.</summary>
internal static class PermissionEvaluatorCeiling
{
    public static bool Contains(string permission) => Authorization.PermissionEvaluator.ServiceRoleCeiling.Contains(permission);
}

/// <summary>
/// Authenticates a <c>Bearer</c> <c>ci_producer</c> token (02.4.1 AC6/AC7): the org, project and
/// permission set come from the token's claims and nowhere else.
/// </summary>
internal sealed class CiProducerAuthenticator(CiProducerTokens tokens) : ICallerAuthenticator
{
    private const string Scheme = "Bearer ";

    /// <inheritdoc />
    public async ValueTask<AuthenticatedCaller?> AuthenticateAsync(ICallerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var header = request.Header("Authorization");
        if (header is null || !header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await tokens.ValidateAsync(header[Scheme.Length..].Trim()).ConfigureAwait(false);
    }
}

/// <summary>The lifetime rule of every token this module validates, against an injectable clock.</summary>
internal static class LifetimeCheck
{
    /// <summary>
    /// True when <paramref name="now"/> is not before <c>nbf - skew</c> and not after <c>exp + skew</c>.
    /// A token without <c>exp</c> is never valid.
    /// </summary>
    public static bool IsWithin(DateTimeOffset now, DateTime? notBefore, DateTime? expires, TimeSpan skew)
    {
        if (expires is null)
        {
            return false;
        }

        var exp = new DateTimeOffset(DateTime.SpecifyKind(expires.Value, DateTimeKind.Utc));
        if (now > exp + skew)
        {
            return false;
        }

        if (notBefore is { } nbf)
        {
            var from = new DateTimeOffset(DateTime.SpecifyKind(nbf, DateTimeKind.Utc));
            if (now < from - skew)
            {
                return false;
            }
        }

        return true;
    }
}
