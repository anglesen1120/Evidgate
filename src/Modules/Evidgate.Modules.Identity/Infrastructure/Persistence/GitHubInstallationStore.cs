using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Application;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>One stored installation row, as the routes read it.</summary>
/// <param name="Id">Surrogate row id used by the disconnect route.</param>
/// <param name="InstallationId">Numeric GitHub installation id.</param>
/// <param name="AccountLogin">Login of the account the App is installed on.</param>
/// <param name="AccountId">Numeric id of that account.</param>
/// <param name="State"><c>active</c>, <c>suspended</c> or <c>deleted</c>.</param>
/// <param name="Permissions">Granted repository permission map.</param>
/// <param name="SuspendedAt">When GitHub suspended the installation, or null for an active one.</param>
internal sealed record GitHubInstallationRow(
    Guid Id,
    long InstallationId,
    string AccountLogin,
    long AccountId,
    string State,
    IReadOnlyDictionary<string, string> Permissions,
    DateTimeOffset? SuspendedAt);

/// <summary>The one-time install nonce store. Only the SHA-256 of the nonce is ever written.</summary>
internal interface IGitHubInstallStateStore
{
    /// <summary>Stores the hash bound to the org, the user and the expiry; false when the user's live states are full.</summary>
    /// <returns><c>true</c> when the state was written.</returns>
    Task<bool> AddAsync(byte[] stateHash, Guid orgId, Guid userId, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the nonce used and reports whether this caller may proceed. One conditional UPDATE is the whole
    /// check, so two concurrent callbacks cannot both succeed.
    /// </summary>
    Task<bool> ConsumeAsync(byte[] stateHash, Guid orgId, Guid userId, CancellationToken cancellationToken);

    /// <summary>Whether this org and user consumed <paramref name="stateHash"/>; read inside the insert transaction.</summary>
    Task<bool> ExistsUsedAsync(byte[] stateHash, Guid orgId, Guid userId, CancellationToken cancellationToken);
}

/// <summary>The org's installation rows, scoped by RLS to the ambient tenant.</summary>
internal interface IGitHubInstallationStore
{
    /// <summary>The org's non-deleted row, or null.</summary>
    Task<GitHubInstallationRow?> FindCurrentAsync(Guid orgId, CancellationToken cancellationToken);

    /// <summary>Writes the row, or reports <c>installation_bound</c> when a unique key already holds it.</summary>
    Task<Result> CreateAsync(Guid orgId, GitHubInstallationRow row, Guid installedBy, CancellationToken cancellationToken);

    /// <summary>Moves a non-deleted row of this org to <c>deleted</c>, returning its installation id or null.</summary>
    Task<long?> DisconnectAsync(Guid orgId, Guid id, CancellationToken cancellationToken);
}

/// <summary>Dapper implementation of both stores and of the token source's installation resolver.</summary>
internal sealed class GitHubInstallationStore(ITenantDb db)
    : IGitHubInstallStateStore, IGitHubInstallationStore, IGitHubInstallationResolver, IGitHubInstallationOwnership
{
    /// <summary>How many unspent install states one user may hold; each start route call adds one.</summary>
    internal const int MaxLiveStatesPerUser = 20;

    private const string CurrentSql = """
        select id,
               installation_id as InstallationId,
               account_login as AccountLogin,
               account_id as AccountId,
               state,
               permissions::text as Permissions
        from public.github_installation
        where org_id = @org_id and state <> 'deleted'
        order by created_at desc
        limit 1
        """;

    private const string InsertSql = """
        insert into public.github_installation
            (id, org_id, installation_id, account_login, account_id, permissions, state, suspended_at, installed_by)
        values
            (@id, @org_id, @installation_id, @account_login, @account_id, @permissions::jsonb, @state,
             @suspended_at, @installed_by)
        on conflict do nothing
        returning id
        """;

    private const string DisconnectSql = """
        update public.github_installation
        set state = 'deleted', suspended_at = null
        where org_id = @org_id and id = @id and state <> 'deleted'
        returning installation_id
        """;

    private const string OwnedActiveSql = """
        select 1
        from public.github_installation
        where org_id = @org_id and installation_id = @installation_id and state = 'active'
        """;

    /// <summary>
    /// Takes the per-(org,user) state lock of the running transaction before the cap is counted: two starts of one
    /// user serialize here, so the count below cannot both read the same under-cap number and both insert. The lock
    /// ends with the transaction, and the count is a later statement, which sees every row committed while waiting.
    /// </summary>
    private const string StateLockSql = """
        select 1 from (select pg_advisory_xact_lock(hashtextextended(@key, 0))) as state_lock
        """;

    private const string AddStateSql = """
        insert into public.github_install_state (state_hash, org_id, user_id, expires_at)
        select @state_hash, @org_id, @user_id, @expires_at
        where (select count(*)
                 from public.github_install_state
                where org_id = @org_id and user_id = @user_id and used_at is null and expires_at > now())
              < @max_live
        returning state_hash
        """;

    private const string ConsumeStateSql = """
        update public.github_install_state
        set used_at = now()
        where state_hash = @state_hash
          and org_id = @org_id
          and user_id = @user_id
          and used_at is null
          and expires_at > now()
        returning state_hash
        """;

    private const string UsedStateSql = """
        select state_hash
        from public.github_install_state
        where state_hash = @state_hash
          and org_id = @org_id
          and user_id = @user_id
          and used_at is not null
        """;

    private sealed record Row(
        Guid Id,
        long InstallationId,
        string AccountLogin,
        long AccountId,
        string State,
        string Permissions);

    /// <inheritdoc />
    public async Task<bool> AddAsync(
        byte[] stateHash,
        Guid orgId,
        Guid userId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        // Serialize this user's starts first, then count and insert in one statement; without the lock two
        // concurrent starts both read the same live count and the cap is exceeded.
        await db.ScalarAsync<int?>(
            StateLockSql,
            new { key = string.Concat(orgId.ToString("D"), ":", userId.ToString("D")) },
            cancellationToken).ConfigureAwait(false);

        var added = await db.ScalarAsync<byte[]>(
            AddStateSql,
            new
            {
                state_hash = stateHash,
                org_id = orgId,
                user_id = userId,
                expires_at = expiresAt,
                max_live = MaxLiveStatesPerUser,
            },
            cancellationToken).ConfigureAwait(false);

        return added is not null;
    }

    /// <inheritdoc />
    public async Task<bool> ConsumeAsync(byte[] stateHash, Guid orgId, Guid userId, CancellationToken cancellationToken)
    {
        var consumed = await db.ScalarAsync<byte[]>(
            ConsumeStateSql,
            new { state_hash = stateHash, org_id = orgId, user_id = userId },
            cancellationToken).ConfigureAwait(false);

        return consumed is not null;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsUsedAsync(byte[] stateHash, Guid orgId, Guid userId, CancellationToken cancellationToken)
    {
        var found = await db.ScalarAsync<byte[]>(
            UsedStateSql,
            new { state_hash = stateHash, org_id = orgId, user_id = userId },
            cancellationToken).ConfigureAwait(false);

        return found is not null;
    }

    /// <inheritdoc />
    public async Task<GitHubInstallationRow?> FindCurrentAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var row = await db.QuerySingleOrDefaultAsync<Row>(CurrentSql, new { org_id = orgId }, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToRow(row);
    }

    /// <inheritdoc />
    public async Task<Result> CreateAsync(Guid orgId, GitHubInstallationRow row, Guid installedBy, CancellationToken cancellationToken)
    {
        var created = await db.ScalarAsync<Guid?>(
            InsertSql,
            new
            {
                id = row.Id,
                org_id = orgId,
                installation_id = row.InstallationId,
                account_login = row.AccountLogin,
                account_id = row.AccountId,
                permissions = JsonSerializer.Serialize(row.Permissions),
                state = row.State,
                suspended_at = row.SuspendedAt,
                installed_by = installedBy,
            },
            cancellationToken).ConfigureAwait(false);

        return created is null ? Result.Failure(GitHubInstallationErrors.Bound) : Result.Success();
    }

    /// <inheritdoc />
    public Task<long?> DisconnectAsync(Guid orgId, Guid id, CancellationToken cancellationToken) =>
        db.ScalarAsync<long?>(DisconnectSql, new { org_id = orgId, id }, cancellationToken);

    /// <inheritdoc />
    public async Task<Result<long>> ResolveAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var row = await FindCurrentAsync(orgId, cancellationToken).ConfigureAwait(false);
        if (row is null || !string.Equals(row.State, "active", StringComparison.Ordinal))
        {
            return Result.Failure<long>(GitHubInstallationErrors.Unavailable);
        }

        return Result.Success(row.InstallationId);
    }

    /// <inheritdoc />
    public async Task<bool> OwnsActiveAsync(Guid orgId, long installationId, CancellationToken cancellationToken)
    {
        if (installationId <= 0)
        {
            return false;
        }

        var owned = await db
            .ScalarAsync<int?>(OwnedActiveSql, new { org_id = orgId, installation_id = installationId }, cancellationToken)
            .ConfigureAwait(false);

        return owned is not null;
    }

    private static GitHubInstallationRow ToRow(Row row)
    {
        IReadOnlyDictionary<string, string> permissions =
            JsonSerializer.Deserialize<Dictionary<string, string>>(row.Permissions) ?? [];

        return new GitHubInstallationRow(
            row.Id,
            row.InstallationId,
            row.AccountLogin,
            row.AccountId,
            row.State,
            permissions,
            SuspendedAt: null);
    }
}
