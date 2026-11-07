using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Authorization;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Evidgate.Modules.Identity.Options;
using Evidgate.Modules.Identity.Tokens;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// <c>POST /v1/projects/{p}/api-tokens</c>: mint a project-scoped credential with an explicit,
/// always-expiring permission subset. The plaintext is answered once and never stored; a retried
/// create mints a second token, which the admin revokes — the same trade the ingest-token create
/// makes, because a remembered response would have to remember the secret.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Name">Label shown in the list.</param>
/// <param name="Permissions">The requested subset; never an administrative code, never more than the creator holds.</param>
/// <param name="ExpiresAt">Hard expiry; required, in the future and within the configured maximum.</param>
internal sealed record CreateApiTokenCommand(
    Guid ProjectId,
    string Name,
    IReadOnlyList<string>? Permissions,
    DateTimeOffset? ExpiresAt)
    : ICommand<Result<ApiTokenCreatedResponse>>;

/// <summary>Shape validation of <see cref="CreateApiTokenCommand"/>: the rules that need no database and no clock.</summary>
internal sealed class CreateApiTokenCommandValidator : AbstractValidator<CreateApiTokenCommand>
{
    /// <summary>The most codes one token may carry (input bound, not a policy).</summary>
    private const int MaxPermissions = 32;

    /// <summary>The longest catalogue code there is room for.</summary>
    private const int MaxCodeLength = 64;

    /// <summary>Builds the rules.</summary>
    public CreateApiTokenCommandValidator()
    {
        RuleFor(command => command.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 120 && !name.Any(char.IsControl))
            .WithErrorCode("invalid");

        RuleFor(command => command.Permissions)
            .Must(codes => codes is null || codes.Count <= MaxPermissions)
            .WithErrorCode("too_many");

        RuleFor(command => command.Permissions)
            .Must(codes => codes is null || codes.All(code => !string.IsNullOrWhiteSpace(code) && code.Length <= MaxCodeLength))
            .WithErrorCode("invalid");
    }
}

/// <summary>
/// Handler of <see cref="CreateApiTokenCommand"/>. Every refusal happens before the row exists, in this
/// order: key material configured, expiry bounds, permission set known, deny set, then the creator's own
/// effective set. The escalation check asks <see cref="ICallerPermissionSet"/> — the same join the C2 gate
/// evaluates — so a token can never hold what the next request would refuse its creator. That one answer also
/// carries the caller's active membership, which is what the row records: <c>created_by</c> is
/// <c>(org_id, created_by) → membership (org_id, id)</c>, a membership and never a platform user, so a caller
/// with no active membership is refused instead of guessed. In V1 a member's roles carry no project
/// dimension, so the org-level set is exactly the creator's set on this project.
/// </summary>
[Permission(Permissions.ApiToken.Write)]
[AuditedAction("api_token.create")]
internal sealed class CreateApiTokenCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    ICallerPermissionSet callerPermissions,
    CiSecrets secrets,
    IOptions<ApiTokenOptions> options,
    TimeProvider clock,
    IAuditTrail audit)
    : ICommandHandler<CreateApiTokenCommand, Result<ApiTokenCreatedResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<ApiTokenCreatedResponse>> Handle(
        CreateApiTokenCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(CreateApiTokenCommand));

        if (!secrets.CanHash)
        {
            return Result.Failure<ApiTokenCreatedResponse>(ApiTokenErrors.NotConfigured);
        }

        var now = clock.GetUtcNow();
        var requested = command.Permissions ?? [];

        if (command.ExpiresAt is not { } expiresAt)
        {
            return Result.Failure<ApiTokenCreatedResponse>(
                ApiTokenErrors.Expiry("required", "expires_at is required: a token always expires"));
        }

        if (ExpiryError(expiresAt, now, options.Value.MaxLifetimeDays) is { } expiry)
        {
            return Result.Failure<ApiTokenCreatedResponse>(expiry);
        }

        if (PermissionError(requested) is { } refused)
        {
            return Result.Failure<ApiTokenCreatedResponse>(refused);
        }

        var caller = principals.Current;
        var orgId = scopes.Require().OrgId;

        if (caller is null)
        {
            return Result.Failure<ApiTokenCreatedResponse>(ApiTokenErrors.NoMembership);
        }

        // `created_by` names the caller's MEMBERSHIP, not the platform user: `api_token_created_by_fk` is
        // (org_id, created_by) → membership (org_id, id), and a founder's or an invitee's membership id is its own
        // value. The id comes from the same join that answers the escalation check below, so the creator recorded and
        // the creator whose permissions are compared are one row.
        var access = await callerPermissions.ResolveAsync(orgId, caller, cancellationToken).ConfigureAwait(false);

        if (access.MembershipId is not { } createdBy)
        {
            return Result.Failure<ApiTokenCreatedResponse>(ApiTokenErrors.NoMembership);
        }

        if (requested.Any(code => !access.Codes.Contains(code)))
        {
            return Result.Failure<ApiTokenCreatedResponse>(ApiTokenErrors.Escalation);
        }

        var generated = ApiTokenFormat.Generate();
        var (version, hash) = secrets.HashCurrent(CiSecrets.Bytes(generated.Secret));
        var token = new ApiToken
        {
            Id = Guid.CreateVersion7(),
            OrgId = orgId,
            ProjectId = command.ProjectId,
            Name = command.Name.Trim(),
            Prefix = generated.Prefix,
            TokenHmac = hash,
            PepperVersion = version,
            Permissions = [.. requested.Distinct(StringComparer.Ordinal)],
            ExpiresAt = expiresAt,
            CreatedBy = createdBy,
        };

        await using var context = contexts.Create();
        await context.ApiTokens.AddAsync(token, cancellationToken).ConfigureAwait(false);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (PgErrors.IsForeignKey(exception, "api_token_project_fk"))
        {
            return Result.Failure<ApiTokenCreatedResponse>(
                Error.NotFound("no such project is visible to this tenant"));
        }
        catch (DbUpdateException exception) when (PgErrors.IsForeignKey(exception, "api_token_created_by_fk"))
        {
            // The membership resolved above was removed between the resolve and this insert. Nothing was written and
            // no other project's answer fits, so the caller is told what the next attempt would answer anyway.
            return Result.Failure<ApiTokenCreatedResponse>(ApiTokenErrors.NoMembership);
        }
        catch (DbUpdateException exception) when (PgErrors.IsUnique(exception))
        {
            // The platform-wide prefix index lost a 62^12 draw; nothing is written and the client retries.
            return Result.Failure<ApiTokenCreatedResponse>(ApiTokenErrors.MintingUnavailable);
        }

        audit.Describe(new AuditObject("api_token", token.Id.ToString("D")));

        return Result.Success(ApiTokenViews.Created(token, generated.Full));
    }

    /// <summary>The expiry refusal, or null when <paramref name="expiresAt"/> is usable.</summary>
    private static Error? ExpiryError(DateTimeOffset expiresAt, DateTimeOffset now, int maxLifetimeDays)
    {
        if (expiresAt <= now)
        {
            return ApiTokenErrors.Expiry("past", "expires_at must be in the future");
        }

        return expiresAt > now.AddDays(maxLifetimeDays)
            ? ApiTokenErrors.Expiry(
                "out_of_range",
                $"expires_at is beyond the configured maximum lifetime of {maxLifetimeDays} days")
            : null;
    }

    /// <summary>The permission-set refusal (empty, unknown, denied), or null when the set may be minted.</summary>
    private static Error? PermissionError(IReadOnlyList<string> requested)
    {
        if (requested.Count == 0)
        {
            return ApiTokenErrors.PermissionsRequired;
        }

        var unknown = requested
            .Where(code => !Permissions.IsKnown(code))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (unknown.Count > 0)
        {
            return ApiTokenErrors.Permissions(
                requested,
                unknown,
                ApiErrorCode.UnknownPermission,
                "unknown",
                "a requested permission is not a catalogue code");
        }

        var denied = requested
            .Where(Permissions.TokenDenied.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return denied.Count > 0
            ? ApiTokenErrors.Permissions(
                requested,
                denied,
                ApiTokenErrorCodes.ForbiddenTokenPermission,
                "forbidden",
                "a token may never carry an administrative or token-managing permission")
            : null;
    }
}
