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
/// <para>
/// <b>Invariant: this call needs a second pooled connection.</b> The caller's scope holds one for the whole request, so
/// the data source must be able to hand out one more than the caller's concurrency. No <c>Maximum Pool Size</c> is
/// configured anywhere in Api or Worker today: the pool is Npgsql's default of 100 connections per data source, which is
/// what this call relies on. Both the pool acquire and the counter's row lock are bounded by 5 s, and either bound
/// expiring is the retryable <c>503 unavailable</c>, never a hang (OI-13-108 tracks the sizing question and stays open
/// with its receiver 10.6.1-T3).
/// </para>
/// <para>
/// <b>Batch and reservation allocation (13.1.1-T5, OI-13-107 — delivered here).</b>
/// <see cref="AllocateManyAsync"/> hands out a block of consecutive numbers and <see cref="ReserveAtLeastAsync"/> raises
/// the counter past a number an import brought with it; both do it in <b>one</b> statement against the counter, so an
/// import of many code-less case rows costs one round trip per block instead of one per case. They keep the design above
/// — own connection, own transaction, own commit, 5 s on the pool acquire and on the counter's row lock — with the same
/// fixed tenant, prefix and <c>lock_timeout</c> preamble <see cref="AllocateAsync"/> issues. One import therefore holds
/// at most <b>two</b> extra connections-in-time, one per member, each released before the next call. That does not
/// change the sizing question: OI-13-108 stays <b>open</b> with its receiver 10.6.1-T3 and is <b>not</b> resolved here.
/// </para>
/// </remarks>
internal sealed class AuthoringCodeAllocator(NpgsqlDataSource dataSource, ITenantScopeAccessor scopes)
{
    private const string SetTenantSql = "select set_config('evidgate.org_id', @org_id, true)";

    /// <summary>
    /// The bound on the pool acquire, the same 5 s as the row lock: the allocator needs a <b>second</b> pooled connection
    /// while the caller still holds its own on the ambient scope. The pool it draws from is Npgsql's default of 100
    /// connections — no <c>Maximum Pool Size</c> is configured (OI-13-108, receiver 10.6.1-T3) — so this bound does not
    /// guard a small pool today; it guards the case where every connection is busy, turning that wait into the retryable
    /// <c>503 unavailable</c> instead of a request that hangs on the data source's own timeout.
    /// </summary>
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(5);

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

        NpgsqlConnection connection;
        try
        {
            // The pool acquire is bounded like the row lock below: a pool with no free connection answers the same
            // retryable 503 instead of hanging the request for the data source's own timeout.
            using var acquire = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            acquire.CancelAfter(AcquireTimeout);
            connection = await dataSource.OpenConnectionAsync(acquire.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AuthoringErrors.Fail<AuthoringCode>(AuthoringErrors.Unavailable());
        }

        await using var connectionScope = connection;
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

    /// <summary>
    /// The largest block one <see cref="AllocateManyAsync"/> call hands out: the 5 000-row cap 13.1.1-T5 puts on one
    /// import file (<c>ImportLimits</c>), so a whole import's fresh codes fit in one call. A caller that needs more
    /// allocates in blocks.
    /// </summary>
    internal const int MaximumBatchSize = 5_000;

    /// <summary>
    /// Allocates <paramref name="count"/> consecutive codes with <b>one</b> statement (13.1.1-T5, OI-13-107). The import
    /// of 13.1.1-T5 hands a fresh number to every code-less case row; one round trip per block is what keeps it inside a
    /// statement budget that does not grow with the file.
    /// </summary>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="projectId">The project whose <c>case_prefix</c> and counter are used.</param>
    /// <param name="count">How many codes to allocate, 1..<see cref="MaximumBatchSize"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// The codes of <c>first .. first + count - 1</c>, in order, or the failures
    /// <see cref="AllocateAsync"/> answers: <c>404</c> for an unknown project, <c>422 /project_id</c> for an empty id,
    /// <c>409 quota_exceeded</c> when the block would leave 10⁹, <c>503 unavailable</c> when another allocator holds the
    /// counter past the 5 s lock timeout.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> is outside 1..<see cref="MaximumBatchSize"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="orgId"/> is not the open scope's organization.</exception>
    internal async ValueTask<Result<IReadOnlyList<AuthoringCode>>> AllocateManyAsync(
        Guid orgId,
        Guid projectId,
        int count,
        CancellationToken cancellationToken = default)
    {
        // A programming error, not a caller's: the importer plans blocks from its own row cap, so an out-of-range count
        // is a bug on this side of the wire and must not become an HTTP status.
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaximumBatchSize);

        if (scopes.Require().OrgId != orgId)
        {
            throw new ArgumentException("the open tenant scope belongs to another organization", nameof(orgId));
        }

        if (projectId == Guid.Empty)
        {
            return AuthoringErrors.Fail<IReadOnlyList<AuthoringCode>>(AuthoringErrors.Invalid(
                "/project_id",
                "required",
                "the project is required"));
        }

        NpgsqlConnection connection;
        try
        {
            // The pool acquire is bounded like the row lock below (see the class remarks).
            using var acquire = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            acquire.CancelAfter(AcquireTimeout);
            connection = await dataSource.OpenConnectionAsync(acquire.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AuthoringErrors.Fail<IReadOnlyList<AuthoringCode>>(AuthoringErrors.Unavailable());
        }

        await using var connectionScope = connection;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await SetTenantAsync(connection, transaction, orgId, cancellationToken).ConfigureAwait(false);

        var prefix = await ReadPrefixAsync(connection, transaction, orgId, projectId, cancellationToken)
            .ConfigureAwait(false);
        if (!prefix.IsSuccess)
        {
            return AuthoringErrors.Fail<IReadOnlyList<AuthoringCode>>(prefix.Error!);
        }

        NumberRow? allocated;
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                AuthoringSql.SetLockTimeout,
                null,
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            allocated = await connection.QuerySingleOrDefaultAsync<NumberRow>(new CommandDefinition(
                AllocateCodeBatchSql,
                new { id = Guid.CreateVersion7(), org_id = orgId, project_id = projectId, n = count },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return AuthoringErrors.Fail<IReadOnlyList<AuthoringCode>>(AuthoringErrors.Unavailable());
        }

        if (allocated is null)
        {
            // The statement's own predicate is the 10⁹ cap: no row means the block would leave the range.
            return AuthoringErrors.Fail<IReadOnlyList<AuthoringCode>>(AuthoringErrors.QuotaExceeded());
        }

        var codes = new List<AuthoringCode>(count);
        for (var offset = 0; offset < count; offset++)
        {
            if (!AuthoringCode.TryFromNumber(prefix.Value, allocated.Number + offset, out var code))
            {
                return AuthoringErrors.Fail<IReadOnlyList<AuthoringCode>>(AuthoringErrors.QuotaExceeded());
            }

            codes.Add(code);
        }

        // The same one commit as AllocateAsync: the block is spent even if the caller's transaction never commits.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success<IReadOnlyList<AuthoringCode>>(codes);
    }

    /// <summary>
    /// Raises the project's counter so that the next allocation is at least <paramref name="minNextValue"/> — never
    /// lowers it — with <b>one</b> statement (13.1.1-T5, OI-13-107). An import that brings an explicit code
    /// (<c>SHOP-4200</c>) must not leave the counter behind it, or the next allocation would hand out a number the
    /// project already uses.
    /// </summary>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="projectId">The project whose counter is raised.</param>
    /// <param name="minNextValue">
    /// The number the next allocation must be at least, i.e. the imported code's number plus one; at most
    /// <see cref="AuthoringCode.MaximumNumber"/> + 1, the last value the counter's CHECK allows.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// <c>true</c> when the counter now reserves at least <paramref name="minNextValue"/> (this call raised it or found
    /// it already past), or the failures of the other members: <c>404</c>, <c>422 /project_id</c>,
    /// <c>409 quota_exceeded</c> when <paramref name="minNextValue"/> is above the 10⁹ + 1 the CHECK allows, and
    /// <c>503 unavailable</c> on the 5 s lock timeout.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minNextValue"/> is below 1.</exception>
    /// <exception cref="ArgumentException"><paramref name="orgId"/> is not the open scope's organization.</exception>
    internal async ValueTask<Result<bool>> ReserveAtLeastAsync(
        Guid orgId,
        Guid projectId,
        long minNextValue,
        CancellationToken cancellationToken = default)
    {
        // A programming error: a counter is 1-based, so nothing below 1 is a value an import could mean.
        ArgumentOutOfRangeException.ThrowIfLessThan(minNextValue, 1);

        if (scopes.Require().OrgId != orgId)
        {
            throw new ArgumentException("the open tenant scope belongs to another organization", nameof(orgId));
        }

        if (projectId == Guid.Empty)
        {
            return AuthoringErrors.Fail<bool>(AuthoringErrors.Invalid(
                "/project_id",
                "required",
                "the project is required"));
        }

        NpgsqlConnection connection;
        try
        {
            // The pool acquire is bounded like the row lock below (see the class remarks).
            using var acquire = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            acquire.CancelAfter(AcquireTimeout);
            connection = await dataSource.OpenConnectionAsync(acquire.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AuthoringErrors.Fail<bool>(AuthoringErrors.Unavailable());
        }

        await using var connectionScope = connection;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await SetTenantAsync(connection, transaction, orgId, cancellationToken).ConfigureAwait(false);

        // The liveness probe of the project, so an unknown project is the same 404 the other two members answer instead
        // of the insert's 23503 turning into a 500. The prefix itself is not needed here.
        var prefix = await ReadPrefixAsync(connection, transaction, orgId, projectId, cancellationToken)
            .ConfigureAwait(false);
        if (!prefix.IsSuccess)
        {
            return AuthoringErrors.Fail<bool>(prefix.Error!);
        }

        long? reserved;
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                AuthoringSql.SetLockTimeout,
                null,
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            reserved = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                ReserveCodeSql,
                new
                {
                    id = Guid.CreateVersion7(),
                    org_id = orgId,
                    project_id = projectId,
                    min_next_value = minNextValue,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return AuthoringErrors.Fail<bool>(AuthoringErrors.Unavailable());
        }

        if (reserved is null)
        {
            // No row means the statement's own range guard refused the reservation: nothing was written.
            return AuthoringErrors.Fail<bool>(AuthoringErrors.QuotaExceeded());
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(true);
    }

    /// <summary>
    /// One block of consecutive numbers (§4's <c>next_value</c> arithmetic, batched): the row is created on first use
    /// with <c>next_value = @n + 1</c> so the block is <c>1 .. n</c>, and an existing row is raised by <c>@n</c> only
    /// when the whole block fits under the 10⁹ cap — <c>1000000001 - @n</c> is the largest <c>next_value</c> whose last
    /// number is still 10⁹, and <c>next_value + @n</c> then stays within the column's <c>1 … 1000000001</c> CHECK. No
    /// row returned is the exhausted project (<c>409 quota_exceeded</c>). The upsert's row lock is what serialises
    /// concurrent blocks, exactly like <c>AllocateCode</c>.
    /// </summary>
    private const string AllocateCodeBatchSql = """
        insert into public.authoring_code_counter as c (id, org_id, project_id, next_value)
        values (@id, @org_id, @project_id, @n + 1)
        on conflict (org_id, project_id) do update
           set next_value = c.next_value + @n
         where c.next_value <= 1000000001 - @n
        returning next_value - @n as number
        """;

    /// <summary>
    /// The reservation of §4's counter: <c>next_value</c> becomes <c>max(next_value, @min_next_value)</c>, so a value
    /// the project has already handed out can never be lowered, and the row is created on the first reservation. Both
    /// arms are guarded by the counter's <c>1 … 1000000001</c> CHECK — the insert by the <c>where</c> of its
    /// <c>select</c>, the update by its own <c>where</c> — so an out-of-range request writes nothing and a missing row is
    /// the <c>409 quota_exceeded</c> of a number the project cannot hand out.
    /// </summary>
    private const string ReserveCodeSql = """
        insert into public.authoring_code_counter as c (id, org_id, project_id, next_value)
        select @id::uuid, @org_id::uuid, @project_id::uuid, @min_next_value::bigint
         where @min_next_value between 1 and 1000000001
        on conflict (org_id, project_id) do update
           set next_value = greatest(c.next_value, excluded.next_value)
         where excluded.next_value <= 1000000001
        returning next_value
        """;

    /// <summary>Applies the tenant setting to the run's own connection, as <see cref="AllocateAsync"/> does.</summary>
    /// <param name="connection">The run's connection.</param>
    /// <param name="transaction">The run's own transaction.</param>
    /// <param name="orgId">The tenant.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private static async ValueTask SetTenantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            SetTenantSql,
            new { org_id = orgId.ToString("D", System.Globalization.CultureInfo.InvariantCulture) },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the project's stored <c>case_prefix</c> on the run's own connection: the liveness probe of the three counter
    /// members (<c>404</c> for a project this tenant cannot see) and the formatting source of the two that return codes.
    /// </summary>
    /// <param name="connection">The run's connection.</param>
    /// <param name="transaction">The run's own transaction.</param>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The prefix, or the <c>404</c> of an unknown project.</returns>
    /// <exception cref="InvalidOperationException">The stored prefix is outside §4's grammar (a schema bug).</exception>
    private static async ValueTask<Result<string>> ReadPrefixAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orgId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var prefix = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            AuthoringSql.ReadCasePrefix,
            new { org_id = orgId, project_id = projectId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (prefix is null)
        {
            return AuthoringErrors.Fail<string>(AuthoringErrors.NotFound(
                "no project with that id is visible in this tenant"));
        }

        if (!AuthoringCode.IsValidPrefix(prefix))
        {
            // A stored prefix outside the grammar is a schema bug, never a caller error (see AllocateAsync).
            throw new InvalidOperationException(
                "the stored case_prefix '" + prefix + "' of project " + projectId.ToString("D")
                + " is outside the grammar of 13.1.1 §4");
        }

        return Result.Success(prefix);
    }

    /// <summary>The allocated number, as <c>returning next_value - 1 as number</c> hands it back.</summary>
    /// <param name="Number">The number this allocation owns; the next allocation takes the following one.</param>
    internal sealed record NumberRow(long Number);
}
