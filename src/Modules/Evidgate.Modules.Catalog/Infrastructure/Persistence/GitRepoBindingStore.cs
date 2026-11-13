using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Application;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Npgsql;

namespace Evidgate.Modules.Catalog.Infrastructure.Persistence;

/// <summary>One binding row as the command writes it; the secret reference is server-derived, never caller-supplied.</summary>
/// <param name="Id">Application-generated identity.</param>
/// <param name="ProjectId">Project the binding belongs to.</param>
/// <param name="InstallationId">Verified GitHub App installation.</param>
/// <param name="RepositoryId">Verified GitHub repository.</param>
/// <param name="DefaultBranch">GitHub's default branch at registration.</param>
/// <param name="WebhookSecretRef">Secret-store reference of the binding's webhook secret.</param>
internal sealed record GitRepoBindingRow(
    Guid Id,
    Guid ProjectId,
    long InstallationId,
    long RepositoryId,
    string DefaultBranch,
    string WebhookSecretRef);

/// <summary>The binding rows, read and written inside the caller's tenant scope so RLS applies to every statement.</summary>
internal interface IGitRepoBindingStore
{
    /// <summary>Inserts one binding, translating the unique keys into the caller's conflict.</summary>
    /// <param name="orgId">The tenant the row belongs to.</param>
    /// <param name="row">The verified binding.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    Task<Result<GitRepoBindingDto>> InsertAsync(Guid orgId, GitRepoBindingRow row, CancellationToken cancellationToken);

    /// <summary>Moves one live binding to <paramref name="state"/>, answering the stored state or null when no row matched.</summary>
    /// <param name="orgId">The tenant the row belongs to.</param>
    /// <param name="projectId">Project the binding must belong to.</param>
    /// <param name="bindingId">The binding to move.</param>
    /// <param name="state">The state the installation event asks for.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    Task<GitRepoSyncState?> SetSyncStateAsync(
        Guid orgId,
        Guid projectId,
        Guid bindingId,
        GitRepoSyncState state,
        CancellationToken cancellationToken);

    /// <summary>The stored state of one binding, or null when it is not visible to the tenant.</summary>
    /// <param name="orgId">The tenant the row belongs to.</param>
    /// <param name="projectId">Project the binding must belong to.</param>
    /// <param name="bindingId">The binding to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<string?> FindSyncStateAsync(
        Guid orgId,
        Guid projectId,
        Guid bindingId,
        CancellationToken cancellationToken);
}

/// <summary>Dapper implementation of <see cref="IGitRepoBindingStore"/>.</summary>
internal sealed class GitRepoBindingStore(ITenantDb db) : IGitRepoBindingStore
{
    private const string InsertSql = """
        insert into public.git_repo_binding
            (id, org_id, project_id, provider, installation_id, repository_id, default_branch, webhook_secret_ref)
        values
            (@id, @org_id, @project_id, 'github', @installation_id, @repository_id, @default_branch, @webhook_secret_ref)
        """;

    private const string SetStateSql = """
        update public.git_repo_binding
        set sync_state = @sync_state
        where org_id = @org_id and project_id = @project_id and id = @id
        returning sync_state
        """;

    private const string ReadStateSql = """
        select sync_state
        from public.git_repo_binding
        where org_id = @org_id and project_id = @project_id and id = @id
        """;

    /// <inheritdoc />
    public async Task<Result<GitRepoBindingDto>> InsertAsync(
        Guid orgId,
        GitRepoBindingRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        try
        {
            await db.ExecuteAsync(
                InsertSql,
                new
                {
                    id = row.Id,
                    org_id = orgId,
                    project_id = row.ProjectId,
                    installation_id = row.InstallationId.ToString(CultureInfo.InvariantCulture),
                    repository_id = row.RepositoryId.ToString(CultureInfo.InvariantCulture),
                    default_branch = row.DefaultBranch,
                    webhook_secret_ref = row.WebhookSecretRef,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // An installation belongs to one org; the global key therefore conflicts with another project in that org.
            return Result.Failure<GitRepoBindingDto>(
                exception.ConstraintName == "git_repo_binding_global_repository_key"
                    ? GitRepoBindingErrors.BoundElsewhere
                    : GitRepoBindingErrors.AlreadyBound);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return Result.Failure<GitRepoBindingDto>(CatalogErrors.ProjectNotFound);
        }

        return Result.Success(
            new GitRepoBindingDto(
                row.Id,
                row.ProjectId,
                row.InstallationId,
                row.RepositoryId,
                row.DefaultBranch,
                GitRepoSyncStates.Active),
            201);
    }

    /// <inheritdoc />
    public async Task<GitRepoSyncState?> SetSyncStateAsync(
        Guid orgId,
        Guid projectId,
        Guid bindingId,
        GitRepoSyncState state,
        CancellationToken cancellationToken)
    {
        var stored = await db.ScalarAsync<string?>(
            SetStateSql,
            new
            {
                org_id = orgId,
                project_id = projectId,
                id = bindingId,
                sync_state = GitRepoSyncStates.ToWire(state),
            },
            cancellationToken).ConfigureAwait(false);

        return GitRepoSyncStates.TryParse(stored, out var parsed) ? parsed : null;
    }

    /// <inheritdoc />
    public Task<string?> FindSyncStateAsync(
        Guid orgId,
        Guid projectId,
        Guid bindingId,
        CancellationToken cancellationToken) =>
        db.ScalarAsync<string?>(
            ReadStateSql,
            new { org_id = orgId, project_id = projectId, id = bindingId },
            cancellationToken);
}
