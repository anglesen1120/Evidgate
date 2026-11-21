using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Evidgate.Modules.Catalog.Application.Commands;

/// <summary>
/// Create a draft in a project (<c>POST /v1/projects/{p}/drafts</c>). The raw body travels unparsed so the pipeline's
/// authorization stage answers an unauthorized caller before any member of it is read; the handler validates it.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Body">The raw request body.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record CreateDraftCommand(Guid ProjectId, JsonElement Body, string IdempotencyKey)
    : ICommand<Result<DraftDto>>, IIdempotentCommand;

/// <summary>
/// Replace a draft's content (<c>PATCH /v1/drafts/{d}</c>). <paramref name="IfMatch"/> is the raw header: parsing it
/// here keeps an unauthorized caller's answer independent of the header it sent.
/// </summary>
/// <param name="DraftId">The draft from the route.</param>
/// <param name="Body">The raw request body.</param>
/// <param name="IfMatch">The raw <c>If-Match</c> header, or null.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record UpdateDraftCommand(Guid DraftId, JsonElement Body, string? IfMatch, string IdempotencyKey)
    : ICommand<Result<DraftDto>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="CreateDraftCommand"/>: 403 for a credential scoped elsewhere, 404 for a project the tenant
/// cannot see, 403 <c>draft_author_required</c> for a principal with no active membership, 422 <c>draft_invalid</c> for
/// a body outside the contract.
/// </summary>
[AuditedAction("draft.create")]
[Permission(Permissions.Draft.Write)]
internal sealed class CreateDraftCommandHandler(
    ITenantDbContextFactory<CatalogDbContext> contexts,
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<CreateDraftCommand, Result<DraftDto>>
{
    /// <summary>The status of a create, kept local so the application layer names no HTTP type.</summary>
    private const int CreatedStatus = 201;

    /// <inheritdoc />
    public async ValueTask<Result<DraftDto>> Handle(CreateDraftCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(CreateDraftCommand));

        if (CatalogScope.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<DraftDto>(mismatch);
        }

        var exists = await db.ScalarAsync<int?>(CatalogReadSql.ProjectExists, new { project_id = command.ProjectId }, cancellationToken)
            .ConfigureAwait(false);
        if (exists is null)
        {
            return Result.Failure<DraftDto>(CatalogErrors.ProjectNotFound);
        }

        var (parsed, bodyError) = DraftBody.ReadCreate(command.Body);
        if (bodyError is not null || parsed is null)
        {
            return Result.Failure<DraftDto>(bodyError!);
        }

        var orgId = scopes.Require().OrgId;
        var author = await DraftActor.MembershipIdAsync(db, principals, orgId, cancellationToken).ConfigureAwait(false);
        if (author is null)
        {
            return Result.Failure<DraftDto>(DraftErrors.AuthorRequired);
        }

        await using var context = contexts.Create();

        var row = new Draft
        {
            Id = Guid.CreateVersion7(),
            OrgId = orgId,
            ProjectId = command.ProjectId,
            AuthorMembershipId = author.Value,
            Kind = parsed.Request.Kind,
            BaseCommit = parsed.Request.BaseCommit,
            Content = parsed.ContentJson,
            Provenance = parsed.ProvenanceJson,
        };

        context.Drafts.Add(row);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // The project vanished between the existence probe and the insert: the same 404 the probe would have answered.
            return Result.Failure<DraftDto>(CatalogErrors.ProjectNotFound);
        }

        return await DraftReader.SavedAsync(db, row.Id, audit, cancellationToken, CreatedStatus).ConfigureAwait(false);
    }
}

/// <summary>
/// Handler of <see cref="UpdateDraftCommand"/>: 404 for another tenant's or another project's draft, 422 for a malformed
/// <c>If-Match</c> or body, 412 for a stale version, 409 <c>draft_state_conflict</c> once the draft left editing.
/// </summary>
[AuditedAction("draft.update")]
[Permission(Permissions.Draft.Write)]
internal sealed class UpdateDraftCommandHandler(
    ITenantDbContextFactory<CatalogDbContext> contexts,
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<UpdateDraftCommand, Result<DraftDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<DraftDto>> Handle(UpdateDraftCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(UpdateDraftCommand));

        await using var context = contexts.Create();

        var row = await context.Drafts
            .SingleOrDefaultAsync(found => found.Id == command.DraftId, cancellationToken)
            .ConfigureAwait(false);

        // Another tenant's id, another project's draft and a missing id are one answer: the caller learns nothing.
        if (row is null || CatalogScope.Project(row.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<DraftDto>(DraftErrors.NotFound);
        }

        if (CatalogPreconditions.IfMatch(command.IfMatch, "draft", out var expectedVersion) is { } ifMatchError)
        {
            return Result.Failure<DraftDto>(ifMatchError);
        }

        if (row.Version != expectedVersion)
        {
            return Result.Failure<DraftDto>(CatalogErrors.Stale("draft", row.Version));
        }

        if (row.Status != DraftStatus.Draft)
        {
            return Result.Failure<DraftDto>(DraftErrors.StateConflict(row.Status));
        }

        var (contentJson, bodyError) = DraftBody.ReadUpdate(command.Body);
        if (bodyError is not null || contentJson is null)
        {
            return Result.Failure<DraftDto>(bodyError!);
        }

        row.ApplyContent(contentJson);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another writer committed first; answer 412 with the version that won, never a second applied edit.
            var latest = await DraftReader.RowAsync(db, command.DraftId, cancellationToken).ConfigureAwait(false);
            return Result.Failure<DraftDto>(CatalogErrors.Stale("draft", latest?.Version ?? row.Version));
        }

        return await DraftReader.SavedAsync(db, command.DraftId, audit, cancellationToken).ConfigureAwait(false);
    }
}
