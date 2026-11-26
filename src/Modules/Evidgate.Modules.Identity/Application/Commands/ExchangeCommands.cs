using System.Security.Cryptography;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// The tenant-side half of the ingest-token exchange (02.4.2 AC2): the endpoint resolved the prefix pre-tenant; this
/// verifies the secret in constant time against the stored HMAC and mints the project-scoped token.
/// </summary>
/// <param name="TokenId">The resolved token.</param>
/// <param name="Secret">The presented secret half.</param>
internal sealed record ExchangeIngestTokenCommand(Guid TokenId, string Secret) : ICommand<Result<CiTokenResponse>>;

[CredentialAuthenticated]
[AuditedAction("ingest_token.exchange")]
internal sealed class ExchangeIngestTokenCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantDb db,
    ITenantScopeAccessor scopes,
    CiSecrets secrets,
    CiProducerTokens tokens,
    TimeProvider clock,
    IAuditTrail audit)
    : ICommandHandler<ExchangeIngestTokenCommand, Result<CiTokenResponse>>
{
    /// <summary><c>last_used_at</c> is rewritten at most this often, so the hot path writes almost never (AC6).</summary>
    private const string TouchSql =
        "update public.ingest_token set last_used_at = now() where id = @id and (last_used_at is null or last_used_at < now() - interval '60 seconds')";

    public async ValueTask<Result<CiTokenResponse>> Handle(ExchangeIngestTokenCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(ExchangeIngestTokenCommand));

        var now = clock.GetUtcNow();
        var orgId = scopes.Require().OrgId;

        await using var context = contexts.Create();
        var token = await context.IngestTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == command.TokenId, cancellationToken)
            .ConfigureAwait(false);

        if (token is null || token.OrgId != orgId)
        {
            return Unauthorized(ApiErrorCode.TokenUnknown, "no such token");
        }

        // The secret is proven first: a holder of only the public prefix learns nothing about the token's state.
        // A retired pepper version no longer verifies (T5): it fails exactly like an unknown token.
        var expected = secrets.HashWithVersion(token.PepperVersion, CiSecrets.Bytes(command.Secret));
        if (expected is null || !CryptographicOperations.FixedTimeEquals(expected, token.TokenHmac))
        {
            return Unauthorized(ApiErrorCode.TokenUnknown, "no such token");
        }

        switch (token.StatusAt(now))
        {
            case "revoked":
                return Unauthorized(ApiErrorCode.TokenRevoked, "this token was revoked");
            case "expired":
                return Unauthorized(ApiErrorCode.TokenExpired, "this token expired");
        }

        var scopesToMint = token.Scopes.All(PermissionEvaluatorCeiling.Contains)
            ? token.Scopes
            : token.Scopes.Where(PermissionEvaluatorCeiling.Contains).ToArray();
        var minted = tokens.Mint(new CiProducerRequest(orgId, token.ProjectId, token.Id, scopesToMint));

        // The statement re-checks the age, but a recently used token skips the round trip altogether.
        if (token.LastUsedAt is null || now - token.LastUsedAt >= TimeSpan.FromSeconds(60))
        {
            await db.ExecuteAsync(TouchSql, new { id = token.Id }, cancellationToken).ConfigureAwait(false);
        }

        audit.Describe(new AuditObject("ingest_token", token.Id.ToString("D"), ProjectId: token.ProjectId));

        return Result.Success(new CiTokenResponse(minted.Token, minted.ExpiresAt, token.ProjectId, scopesToMint));
    }

    private static Result<CiTokenResponse> Unauthorized(string code, string message) =>
        Result.Failure<CiTokenResponse>(new Error(code, message, StatusCodes.Status401Unauthorized));
}

/// <summary>Errors shared by the CI commands.</summary>
internal static class CiErrors
{
    public static Error NotConfigured { get; } = new(
        ApiErrorCode.Unavailable,
        "CI authentication is not configured on this deployment",
        StatusCodes.Status503ServiceUnavailable,
        Retryable: true);
}
