using Dapper;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>
/// The manual case code allocator of 13.1.1 §4 (AC4): one counter row per project, allocated under its row lock so two
/// concurrent creates never share a number, bounded by a 5 s <c>lock_timeout</c> so a stuck holder answers
/// <c>503 unavailable</c> instead of piling up (<c>SuiteCommands.cs:161</c> precedent).
/// </summary>
/// <remarks>
/// <para>
/// <b>It commits on its own connection, deliberately.</b> Plan 13.1.1 §4 [DECISION] accepts that a number is burned
/// when the caller's transaction rolls back; inside the caller's transaction the bump would roll back with it and the
/// number would be handed out again, which is exactly the "not gapless" reading the plan rejects. So the allocation runs
/// in its own transaction on the app-role data source with the tenant setting applied to it (the
/// <c>NpgsqlMemberOrgResolver</c> shape of a second connection), commits, and returns — a caller that then fails leaves
/// a gap, never a duplicate.
/// </para>
/// <para>
/// The organization must still equal the open scope's: an allocation outside a request's tenant flow is a bug.
/// </para>
/// </remarks>
internal sealed class AuthoringCodeAllocator(NpgsqlDataSource dataSource, ITenantScopeAccessor scopes)
{
    private const string SetTenantSql = "select set_config('evidgate.org_id', @org_id, true)";

    /// <summary>Allocates the project's next code.</summary>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="projectId">The project whose <c>case_prefix</c> and counter are used.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async ValueTask<Result<AuthoringCode>> AllocateAsync(
        Guid orgId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (scopes.Require().OrgId != orgId)
        {
            throw new ArgumentException("the open tenant scope belongs to another organization", nameof(orgId));
        }

        if (projectId == Guid.Empty)
        {
            return AuthoringErrors.Fail<AuthoringCode>(AuthoringErrors.Invalid(
                "/project_id",
                "required",
                "the project is required"));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            SetTenantSql,
            new { org_id = orgId.ToString("D", System.Globalization.CultureInfo.InvariantCulture) },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var prefix = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            AuthoringSql.ReadCasePrefix,
            new { org_id = orgId, project_id = projectId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (prefix is null)
        {
            return AuthoringErrors.Fail<AuthoringCode>(AuthoringErrors.NotFound(
                "no project with that id is visible in this tenant"));
        }

        if (!AuthoringCode.IsValidPrefix(prefix))
        {
            // A stored prefix outside the grammar is a schema bug (the column CHECK would have refused it), never a
            // caller error: fail loudly instead of allocating a code the format check would reject.
            throw new InvalidOperationException(
                "the stored case_prefix '" + prefix + "' of project " + projectId.ToString("D")
                + " is outside the grammar of 13.1.1 §4");
        }

        NumberRow? allocated;
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                AuthoringSql.SetLockTimeout,
                null,
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            await connection.ExecuteAsync(new CommandDefinition(
                AuthoringSql.EnsureCodeCounter,
                new { id = Guid.CreateVersion7(), org_id = orgId, project_id = projectId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            allocated = await connection.QuerySingleOrDefaultAsync<NumberRow>(new CommandDefinition(
                AuthoringSql.AllocateCode,
                new { org_id = orgId, project_id = projectId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return AuthoringErrors.Fail<AuthoringCode>(AuthoringErrors.Unavailable());
        }

        if (allocated is null)
        {
            // The counter's own CHECK caps it at 10⁹ + 1, so "no row" is exactly the exhausted project.
            return AuthoringErrors.Fail<AuthoringCode>(AuthoringErrors.QuotaExceeded());
        }

        if (!AuthoringCode.TryFromNumber(prefix, allocated.Number, out var code))
        {
            return AuthoringErrors.Fail<AuthoringCode>(AuthoringErrors.QuotaExceeded());
        }

        // The one commit of this type: the number is spent even if the caller's transaction never commits.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(code);
    }

    /// <summary>The allocated number, as <c>returning next_value - 1 as number</c> hands it back.</summary>
    /// <param name="Number">The number this allocation owns; the next allocation takes the following one.</param>
    internal sealed record NumberRow(long Number);
}
