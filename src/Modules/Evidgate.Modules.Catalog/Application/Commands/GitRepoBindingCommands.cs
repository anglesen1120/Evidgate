using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Idempotency;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using FluentValidation;
using Mediator;

namespace Evidgate.Modules.Catalog.Application.Commands;

/// <summary>
/// The first unit of <c>POST /v1/projects/{p}/git-repo-bindings</c>: proves under the route's permission that the
/// installation is this org's own live one, before any GitHub call is made and before anything is written. It reads
/// only, so it is a query: a refusal here writes and audits nothing.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="InstallationId">The installation the caller installed the App on.</param>
/// <param name="RepositoryId">The repository the caller names; checked here so a missing id is refused before GitHub.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>, read to answer a retry without a GitHub call.</param>
internal sealed record PrepareGitRepoBindingQuery(
    Guid ProjectId,
    long InstallationId,
    long RepositoryId,
    string IdempotencyKey)
    : IQuery<Result<bool>>;

/// <summary>Shape validation of <see cref="PrepareGitRepoBindingQuery"/>: both ids are positive before any read.</summary>
internal sealed class PrepareGitRepoBindingQueryValidator : AbstractValidator<PrepareGitRepoBindingQuery>
{
    /// <summary>Builds the rules.</summary>
    public PrepareGitRepoBindingQueryValidator()
    {
        RuleFor(query => query.InstallationId).GreaterThan(0).WithErrorCode("out_of_range");
        RuleFor(query => query.RepositoryId).GreaterThan(0).WithErrorCode("out_of_range");
    }
}

/// <summary>The write unit: records the binding the GitHub verification already accepted.</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="InstallationId">The installation the ownership unit proved.</param>
/// <param name="RepositoryId">The repository the verification read through that installation.</param>
/// <param name="DefaultBranch">GitHub's default branch for the repository, or null on a probe that only replays.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>; the only request member the fingerprint covers.</param>
/// <remarks>
/// <see cref="DefaultBranch"/> is excluded from the request fingerprint: it is GitHub's mutable answer, so folding it in
/// would turn a retry after a branch rename, or one racing an outage, into <c>idempotency_conflict</c> before the replay.
/// </remarks>
internal sealed record RegisterGitRepoBindingCommand(
    Guid ProjectId,
    long InstallationId,
    long RepositoryId,
    [property: JsonIgnore] string? DefaultBranch,
    string IdempotencyKey)
    : ICommand<Result<GitRepoBindingDto>>, IIdempotentCommand
{
    /// <summary>
    /// The operation the key is claimed for, named once: the first unit reads the stored answer through it before this
    /// command exists, and the behaviour claims the key with it.
    /// </summary>
    internal static readonly string OperationName = typeof(RegisterGitRepoBindingCommand).FullName!;

    /// <inheritdoc />
    string IIdempotentCommand.Operation => OperationName;
}

/// <summary>Moves a binding's sync state; only a GitHub installation event produces this command.</summary>
/// <param name="ProjectId">The project that holds the binding.</param>
/// <param name="BindingId">The binding the event names.</param>
/// <param name="State">The state the event asks for.</param>
internal sealed record SetGitRepoBindingSyncStateCommand(Guid ProjectId, Guid BindingId, GitRepoSyncState State)
    : ICommand<Result>;

/// <summary>Shape validation of <see cref="RegisterGitRepoBindingCommand"/>; every rule is the column's CHECK.</summary>
internal sealed class RegisterGitRepoBindingCommandValidator : AbstractValidator<RegisterGitRepoBindingCommand>
{
    /// <summary>Builds the rules.</summary>
    public RegisterGitRepoBindingCommandValidator()
    {
        RuleFor(command => command.InstallationId).GreaterThan(0).WithErrorCode("out_of_range");
        RuleFor(command => command.RepositoryId).GreaterThan(0).WithErrorCode("out_of_range");
        // Absent only on the replay probe, which writes nothing; a verified branch must still fit the column.
        RuleFor(command => command.DefaultBranch)
            .Must(branch => branch is null || (!string.IsNullOrWhiteSpace(branch) && branch.Length <= 255))
            .WithErrorCode("invalid_length");
    }
}

/// <summary>
/// Handler of <see cref="PrepareGitRepoBindingQuery"/>: read-only checks, so a refusal writes and audits nothing.
/// </summary>
[Permission(Permissions.Project.Write)]
internal sealed class PrepareGitRepoBindingQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IGitHubInstallationOwnership installations,
    IIdempotencyStore idempotency)
    : IQueryHandler<PrepareGitRepoBindingQuery, Result<bool>>
{
    /// <inheritdoc />
    public async ValueTask<Result<bool>> Handle(
        PrepareGitRepoBindingQuery query,
        CancellationToken cancellationToken)
    {
        if (CatalogScope.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<bool>(mismatch);
        }

        if (!await ProjectExistsAsync(db, query.ProjectId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<bool>(CatalogErrors.ProjectNotFound);
        }

        // Active only: an unknown id, another org's installation, and this org's own suspended one all refuse here with
        // the same 403, so no later stage has a "no live installation" case to answer with a separate code.
        var owned = await installations
            .OwnsActiveAsync(scopes.Require().OrgId, query.InstallationId, cancellationToken)
            .ConfigureAwait(false);
        if (!owned)
        {
            return Result.Failure<bool>(GitRepoBindingErrors.InstallationNotOwned);
        }

        // A retry is answered before the verification: the original request already wrote its row, and GitHub may be
        // unreachable or the branch renamed since. Reading the record claims nothing, so a failed verification that
        // follows leaves no incomplete idempotency row behind. A blank key owns no record — the write unit's gate
        // answers its 400, so nothing is probed here.
        if (string.IsNullOrWhiteSpace(query.IdempotencyKey))
        {
            return Result.Success(false);
        }

        var replay = await idempotency
            .ReplayAsync(RegisterGitRepoBindingCommand.OperationName, query.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(replay is not null);
    }

    /// <summary>Whether the project is visible to this tenant; another org's id answers false, hence the same 404.</summary>
    internal static async Task<bool> ProjectExistsAsync(ITenantDb db, Guid projectId, CancellationToken cancellationToken) =>
        await db
            .ScalarAsync<int?>(CatalogReadSql.ProjectExists, new { project_id = projectId }, cancellationToken)
            .ConfigureAwait(false) is not null;
}

/// <summary>
/// Handler of <see cref="RegisterGitRepoBindingCommand"/>: re-proves ownership inside the write transaction, then
/// inserts the binding and its audit row together. The webhook secret reference is derived from the binding id here,
/// so no request member can name or supply it.
/// </summary>
[Permission(Permissions.Project.Write)]
[AuditedAction("git_repo_binding.create")]
internal sealed class RegisterGitRepoBindingCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IGitHubInstallationOwnership installations,
    IGitRepoBindingStore bindings,
    IAuditTrail audit)
    : ICommandHandler<RegisterGitRepoBindingCommand, Result<GitRepoBindingDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<GitRepoBindingDto>> Handle(
        RegisterGitRepoBindingCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(
            ModuleName.Catalog,
            nameof(RegisterGitRepoBindingCommand));

        var orgId = scopes.Require().OrgId;

        if (CatalogScope.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<GitRepoBindingDto>(mismatch);
        }

        if (!await PrepareGitRepoBindingQueryHandler
                .ProjectExistsAsync(db, command.ProjectId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result.Failure<GitRepoBindingDto>(CatalogErrors.ProjectNotFound);
        }

        // The replay probe sends no verified branch. If retention removed the response meanwhile, refuse the write.
        var defaultBranch = command.DefaultBranch;
        if (defaultBranch is null)
        {
            return Result.Failure<GitRepoBindingDto>(Error.Conflict(
                ApiErrorCode.IdempotencyConflict, "the stored response expired; retry registration"));
        }

        // The verification ran outside this transaction against the installation this proves; re-checking here keeps
        // the write bound to the same org even if the row moved in between.
        if (!await installations
                .OwnsActiveAsync(orgId, command.InstallationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result.Failure<GitRepoBindingDto>(GitRepoBindingErrors.InstallationNotOwned);
        }

        var id = Guid.CreateVersion7();
        var created = await bindings
            .InsertAsync(
                orgId,
                new GitRepoBindingRow(
                    id,
                    command.ProjectId,
                    command.InstallationId,
                    command.RepositoryId,
                    defaultBranch,
                    GitRepoSyncStates.WebhookSecretReference(id)),
                cancellationToken)
            .ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            return created;
        }

        audit.Describe(new AuditObject("git_repo_binding", id.ToString("D")));

        return created;
    }
}

/// <summary>
/// Handler of <see cref="SetGitRepoBindingSyncStateCommand"/>: one column moves through the binding table's guard, and
/// the row, its catalog and its history stay where they are.
/// </summary>
[Permission(Permissions.Project.Write)]
[AuditedAction("git_repo_binding.sync_state")]
internal sealed class SetGitRepoBindingSyncStateCommandHandler(
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IGitRepoBindingStore bindings,
    IAuditTrail audit)
    : ICommandHandler<SetGitRepoBindingSyncStateCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(
        SetGitRepoBindingSyncStateCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(
            ModuleName.Catalog,
            nameof(SetGitRepoBindingSyncStateCommand));

        if (CatalogScope.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure(mismatch);
        }

        var stored = await bindings
            .SetSyncStateAsync(
                scopes.Require().OrgId,
                command.ProjectId,
                command.BindingId,
                command.State,
                cancellationToken)
            .ConfigureAwait(false);
        if (stored is null)
        {
            return Result.Failure(GitRepoBindingErrors.BindingNotFound);
        }

        audit.Describe(new AuditObject("git_repo_binding", command.BindingId.ToString("D")));

        return Result.Success();
    }
}
