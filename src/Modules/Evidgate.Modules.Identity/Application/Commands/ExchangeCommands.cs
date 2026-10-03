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
using Evidgate.Modules.Identity.Domain;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>The permissions an exchange may mint: a GitHub token always gets the whole <c>ci_producer</c> set (AC6).</summary>
internal static class CiProducerPermissions
{
    public static readonly IReadOnlyList<string> Set = [Permissions.Run.Create, Permissions.Event.Write, Permissions.Artifact.Write];
}

/// <summary>
/// The tenant-side half of the GitHub exchange (02.4.1-T3.2). The endpoint validated the OIDC token (outside any
/// transaction, P6) and resolved the binding pre-tenant; this command re-checks everything inside the tenant scope:
/// the binding's state, the proof-of-control challenge (T70), the ref and workflow allowlists, and the replay arbiter.
/// </summary>
/// <param name="BindingId">The resolved binding.</param>
/// <param name="Claims">The verified token claims.</param>
/// <param name="Challenge">The presented challenge, if any.</param>
internal sealed record ExchangeGitHubTokenCommand(Guid BindingId, GitHubOidcClaims Claims, string? Challenge)
    : ICommand<Result<CiTokenResponse>>;

[CredentialAuthenticated]
[AuditedAction("ci_token.exchange")]
internal sealed class ExchangeGitHubTokenCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantScopeAccessor scopes,
    CiSecrets secrets,
    CiProducerTokens tokens,
    TimeProvider clock,
    IAuditTrail audit)
    : ICommandHandler<ExchangeGitHubTokenCommand, Result<CiTokenResponse>>
{
    public async ValueTask<Result<CiTokenResponse>> Handle(ExchangeGitHubTokenCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(ExchangeGitHubTokenCommand));

        var now = clock.GetUtcNow();
        var run = command.Claims.Run;
        var orgId = scopes.Require().OrgId;

        await using var context = contexts.Create();
        var binding = await context.CiBindings
            .SingleOrDefaultAsync(row => row.Id == command.BindingId, cancellationToken)
            .ConfigureAwait(false);

        // The resolver chose the binding by repository id; confirm it still belongs to this tenant and repository.
        if (binding is null || binding.OrgId != orgId || binding.RepositoryId != run.RepositoryId)
        {
            return Fail(ApiErrorCode.BindingMismatch, "no binding admits this repository");
        }

        switch (binding.Status)
        {
            case CiBindingStatus.Disabled:
                return Fail(ApiErrorCode.BindingDisabled, "the binding for this repository is disabled");

            case CiBindingStatus.PendingVerification:
                if (!ChallengeMatches(binding, command.Challenge))
                {
                    return Fail(ApiErrorCode.BindingPending, "the binding has not proven control of the repository yet");
                }

                binding.Status = CiBindingStatus.Active;
                binding.ActivatedAt = now;
                binding.BindingChallengeHash = null;
                binding.ChallengePepperVersion = null;
                break;
        }

        if (!RefGlob.IsAnyMatch(binding.AllowedRefPatterns, run.Ref)
            || !RefGlob.IsAnyMatch(binding.AllowedWorkflowRefs, run.JobWorkflowRef))
        {
            // Includes fork PRs: a fork runs under another repository_id (no binding) or an unlisted workflow.
            return Fail(ApiErrorCode.BindingMismatch, "this ref or workflow is not allowed to mint tokens");
        }

        // D4: trust follows the id. A renamed repository still mints; the new name is stored for display.
        string? renamedFrom = null;
        if (!string.Equals(binding.RepositoryFullName, run.Repository, StringComparison.OrdinalIgnoreCase))
        {
            renamedFrom = binding.RepositoryFullName;
            binding.RepositoryFullName = run.Repository.Length is >= 3 and <= 200 ? run.Repository : binding.RepositoryFullName;
        }

        var minted = tokens.Mint(new CiProducerRequest(
            orgId,
            binding.ProjectId,
            binding.Id,
            binding.Provider,
            CiProducerPermissions.Set,
            Guid.CreateVersion7().ToString("D"),
            run));

        await context.CiTokenIssues.AddAsync(
            new CiTokenIssue
            {
                Id = Guid.CreateVersion7(),
                OrgId = orgId,
                ProjectId = binding.ProjectId,
                BindingId = binding.Id,
                Provider = binding.Provider,
                Jti = command.Claims.Jti,
                RepositoryId = run.RepositoryId,
                RepositoryFullName = run.Repository.Length > 200 ? run.Repository[..200] : run.Repository,
                Ref = run.Ref,
                Sha = run.Sha,
                RunId = run.RunId,
                RunAttempt = run.RunAttempt,
                JobWorkflowRef = run.JobWorkflowRef,
                ExpiresAt = minted.ExpiresAt,
            },
            cancellationToken).ConfigureAwait(false);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (PgErrors.IsUnique(exception, "ci_token_issue_jti_key"))
        {
            return Result.Failure<CiTokenResponse>(new Error(
                ApiErrorCode.TokenReplayed, "this OIDC token was already exchanged", StatusCodes.Status401Unauthorized));
        }
        catch (DbUpdateException exception) when (PgErrors.IsUnique(exception, "ci_binding_active_repository_key"))
        {
            return Result.Failure<CiTokenResponse>(Error.Conflict(
                ApiErrorCode.BindingConflict, "another organization already holds the active binding of this repository"));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<CiTokenResponse>(Error.Conflict(ApiErrorCode.BindingConflict, "the binding changed during the exchange; retry"));
        }

        audit.Describe(new AuditObject(
            "ci_binding",
            binding.Id.ToString("D"),
            Metadata: renamedFrom is null ? null : new Dictionary<string, string> { ["reason"] = "repository_renamed" }));

        return Result.Success(new CiTokenResponse(minted.Token, minted.ExpiresAt, binding.ProjectId, CiProducerPermissions.Set));
    }

    private static Result<CiTokenResponse> Fail(string code, string message) =>
        Result.Failure<CiTokenResponse>(Error.Denied(code, message));

    private bool ChallengeMatches(CiBinding binding, string? challenge)
    {
        if (string.IsNullOrEmpty(challenge)
            || binding.BindingChallengeHash is not { } stored
            || binding.ChallengePepperVersion is not { } version)
        {
            return false;
        }

        var candidate = secrets.HashWithVersion(version, CiSecrets.Bytes(challenge));

        return candidate is not null && CryptographicOperations.FixedTimeEquals(candidate, stored);
    }
}

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

        var minted = tokens.Mint(new CiProducerRequest(
            orgId,
            token.ProjectId,
            token.Id,
            "ingest_token",
            [.. token.Scopes.Where(PermissionEvaluatorCeiling.Contains)],
            Guid.CreateVersion7().ToString("D"),
            Run: null));

        // The statement re-checks the age, but a recently used token skips the round trip altogether.
        if (token.LastUsedAt is null || now - token.LastUsedAt >= TimeSpan.FromSeconds(60))
        {
            await db.ExecuteAsync(TouchSql, new { id = token.Id }, cancellationToken).ConfigureAwait(false);
        }

        audit.Describe(new AuditObject("ingest_token", token.Id.ToString("D")));

        return Result.Success(new CiTokenResponse(minted.Token, minted.ExpiresAt, token.ProjectId, [.. token.Scopes]));
    }

    private static Result<CiTokenResponse> Unauthorized(string code, string message) =>
        Result.Failure<CiTokenResponse>(new Error(code, message, StatusCodes.Status401Unauthorized));
}
