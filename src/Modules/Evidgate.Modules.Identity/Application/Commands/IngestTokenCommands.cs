using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Application;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using FluentValidation;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// The scopes an ingest token may carry; the default is all of them (02.4.2 §4). They are the <c>ci_producer</c> set, so
/// an exchange from an ingest token can read the decision of its own project's runs and a suite revision's execution plan.
/// </summary>
internal static class IngestTokenScopes
{
    public static readonly IReadOnlyList<string> All =
    [
        Permissions.Artifact.Write,
        Permissions.Decision.Read,
        Permissions.Event.Write,
        Permissions.Run.Create,
        Permissions.Suite.PlanRead,
    ];
}

/// <summary>
/// <c>POST /v1/projects/{p}/ingest-tokens</c> (02.4.2 AC1). Like binding creation it is NOT run through the
/// response-replay store: the response carries the plaintext secret, which must never be persisted (S3). A retried
/// create makes a second token, which the admin can revoke.
/// </summary>
internal sealed record CreateIngestTokenCommand(
    Guid ProjectId,
    string Name,
    IReadOnlyList<string>? Scopes,
    DateTimeOffset? ExpiresAt)
    : ICommand<Result<IngestTokenCreatedResponse>>;

internal sealed class CreateIngestTokenCommandValidator : AbstractValidator<CreateIngestTokenCommand>
{
    public CreateIngestTokenCommandValidator()
    {
        RuleFor(command => command.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && !name.Any(char.IsControl))
            .WithErrorCode("invalid");
        When(command => command.Scopes is not null, () =>
            RuleFor(command => command.Scopes)
                .Must(scopes => scopes is { Count: > 0 } && scopes.Count <= IngestTokenScopes.All.Count
                    && scopes.All(scope => IngestTokenScopes.All.Contains(scope))
                    && scopes.Distinct().Count() == scopes.Count)
                .WithErrorCode("invalid"));
    }
}

[Permission(Permissions.IngestToken.Write)]
[AuditedAction("ingest_token.create")]
internal sealed class CreateIngestTokenCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    CiSecrets secrets,
    TimeProvider clock,
    IAuditTrail audit)
    : ICommandHandler<CreateIngestTokenCommand, Result<IngestTokenCreatedResponse>>
{
    public async ValueTask<Result<IngestTokenCreatedResponse>> Handle(
        CreateIngestTokenCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(CreateIngestTokenCommand));

        if (!secrets.CanHash)
        {
            return Result.Failure<IngestTokenCreatedResponse>(CiErrors.NotConfigured);
        }

        var now = clock.GetUtcNow();
        if (command.ExpiresAt is { } expires && expires <= now)
        {
            return Result.Failure<IngestTokenCreatedResponse>(Error.Invalid(
                "expires_at must be in the future", [new ErrorDetail("expires_at", "past")]));
        }

        var orgId = scopes.Require().OrgId;
        var (generated, entity) = NewToken(command.ProjectId, orgId, command.Name, command.Scopes, command.ExpiresAt, principals.Current?.UserId);

        await using var context = contexts.Create();
        await context.IngestTokens.AddAsync(entity, cancellationToken).ConfigureAwait(false);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (PgErrors.IsForeignKey(exception))
        {
            return Result.Failure<IngestTokenCreatedResponse>(Error.NotFound("no such project is visible to this tenant"));
        }

        audit.Describe(new AuditObject("ingest_token", entity.Id.ToString("D")));

        return Result.Success(new IngestTokenCreatedResponse(entity.ToView(now), generated.Full, OverlapEndsAt: null), status: 201);
    }

    private (IngestTokenFormat.Generated Generated, IngestToken Entity) NewToken(
        Guid projectId,
        Guid orgId,
        string name,
        IReadOnlyList<string>? requestedScopes,
        DateTimeOffset? expiresAt,
        Guid? createdBy)
    {
        var generated = IngestTokenFormat.Generate();
        var (version, hash) = secrets.HashCurrent(CiSecrets.Bytes(generated.Secret));

        return (generated, new IngestToken
        {
            Id = Guid.CreateVersion7(),
            OrgId = orgId,
            ProjectId = projectId,
            Name = name.Trim(),
            Prefix = generated.Prefix,
            TokenHmac = hash,
            PepperVersion = version,
            Scopes = [.. requestedScopes ?? IngestTokenScopes.All],
            ExpiresAt = expiresAt,
            CreatedBy = createdBy,
        });
    }
}

/// <summary>
/// <c>POST /v1/ingest-tokens/{t}/rotate</c> (02.4.2 AC3): mints a successor; the old token keeps verifying for the
/// overlap window (24 h, D-A5) and then expires. Not response-replayed, for the same reason as create.
/// </summary>
internal sealed record RotateIngestTokenCommand(Guid TokenId) : ICommand<Result<IngestTokenCreatedResponse>>;

[Permission(Permissions.IngestToken.Write)]
[AuditedAction("ingest_token.rotate")]
internal sealed class RotateIngestTokenCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    CiSecrets secrets,
    TimeProvider clock,
    IAuditTrail audit)
    : ICommandHandler<RotateIngestTokenCommand, Result<IngestTokenCreatedResponse>>
{
    /// <summary>How long the previous token keeps verifying after a rotation (DECISIONS §8.1 D-A5).</summary>
    internal static readonly TimeSpan Overlap = TimeSpan.FromHours(24);

    public async ValueTask<Result<IngestTokenCreatedResponse>> Handle(
        RotateIngestTokenCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(RotateIngestTokenCommand));

        if (!secrets.CanHash)
        {
            return Result.Failure<IngestTokenCreatedResponse>(CiErrors.NotConfigured);
        }

        var now = clock.GetUtcNow();
        await using var context = contexts.Create();
        var old = await context.IngestTokens
            .SingleOrDefaultAsync(row => row.Id == command.TokenId, cancellationToken)
            .ConfigureAwait(false);

        if (old is null)
        {
            return Result.Failure<IngestTokenCreatedResponse>(Error.NotFound("no ingest token with that id is visible to this tenant"));
        }

        switch (old.StatusAt(now))
        {
            case "revoked":
                return Result.Failure<IngestTokenCreatedResponse>(Error.Conflict(ApiErrorCode.TokenRevoked, "a revoked token cannot be rotated"));
            case "rotated" or "expired":
                return Result.Failure<IngestTokenCreatedResponse>(Error.Conflict(
                    ApiErrorCode.InvalidTransition, "only an active token can be rotated"));
        }

        var orgId = scopes.Require().OrgId;
        var generated = IngestTokenFormat.Generate();
        var (version, hash) = secrets.HashCurrent(CiSecrets.Bytes(generated.Secret));
        var successor = new IngestToken
        {
            Id = Guid.CreateVersion7(),
            OrgId = orgId,
            ProjectId = old.ProjectId,
            Name = old.Name,
            Prefix = generated.Prefix,
            TokenHmac = hash,
            PepperVersion = version,
            Scopes = old.Scopes,
            ExpiresAt = old.ExpiresAt,
            CreatedBy = principals.Current?.UserId,
        };

        var overlapEnds = now + Overlap;
        await context.IngestTokens.AddAsync(successor, cancellationToken).ConfigureAwait(false);

        try
        {
            // The successor first: the old row's composite foreign key points at it, and EF does not model that
            // relation, so one SaveChanges could order the UPDATE before the INSERT.
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            old.RotatedToId = successor.Id;
            old.OverlapEndsAt = overlapEnds;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<IngestTokenCreatedResponse>(Error.PreconditionFailed("the token changed; retry"));
        }

        audit.Describe(new AuditObject("ingest_token", old.Id.ToString("D")));

        return Result.Success(new IngestTokenCreatedResponse(successor.ToView(now), generated.Full, overlapEnds));
    }
}

/// <summary>
/// <c>DELETE /v1/ingest-tokens/{t}</c> (02.4.2 AC4): revocation is immediate. A token that was rotated cannot also be
/// revoked (a table check), so revoking it ends its overlap window now.
/// </summary>
internal sealed record RevokeIngestTokenCommand(Guid TokenId, string IdempotencyKey)
    : ICommand<Result>, IIdempotentCommand;

[Permission(Permissions.IngestToken.Write)]
[AuditedAction("ingest_token.revoke")]
internal sealed class RevokeIngestTokenCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    TimeProvider clock,
    IAuditTrail audit)
    : ICommandHandler<RevokeIngestTokenCommand, Result>
{
    public async ValueTask<Result> Handle(RevokeIngestTokenCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(RevokeIngestTokenCommand));

        var now = clock.GetUtcNow();
        await using var context = contexts.Create();
        var token = await context.IngestTokens
            .SingleOrDefaultAsync(row => row.Id == command.TokenId, cancellationToken)
            .ConfigureAwait(false);

        if (token is null)
        {
            return Result.Failure(Error.NotFound("no ingest token with that id is visible to this tenant"));
        }

        if (token.RevokedAt is not null || (token.RotatedToId is not null && token.OverlapEndsAt <= now))
        {
            audit.Suppress();

            return Result.Success(StatusCodes.Status204NoContent);
        }

        if (token.RotatedToId is not null)
        {
            token.OverlapEndsAt = now;
        }
        else
        {
            token.RevokedAt = now;
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(Error.PreconditionFailed("the token changed; retry"));
        }

        audit.Describe(new AuditObject("ingest_token", token.Id.ToString("D")));

        return Result.Success(StatusCodes.Status204NoContent);
    }
}
