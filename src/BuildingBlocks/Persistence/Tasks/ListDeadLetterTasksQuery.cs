using System.Globalization;
using Dapper;
using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// One dead-lettered task row as the operator list serves it: one <c>items</c> entry of the
/// response. The mechanism columns that an operator does not need
/// (locked_until, payload, schedule fields, version) stay in the table.
/// </summary>
public sealed record DeadLetterTaskItem(
    Guid Id,
    Guid? OrgId,
    string Queue,
    string TaskType,
    string IdempotencyKey,
    int Attempts,
    int MaxAttempts,
    string? LastError,
    DateTime CreatedAt);

/// <summary>
/// One page of the operator dead-letter list: the items plus the
/// opaque continuation cursor, or null when this page is the last.
/// </summary>
public sealed record DeadLetterTaskPage(IReadOnlyList<DeadLetterTaskItem> Items, string? NextCursor);

internal static class DeadLetterTaskCursor
{
    internal static string Encode(DateTime createdAt, Guid id)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{createdAt:o}|{id}");

        // base64url: the cursor travels in a query string, so '+' and '/' are
        // replaced instead of asking every client to percent-encode the value.
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>Decodes <paramref name="cursor"/>; false marks any value this endpoint did not produce.</summary>
    internal static bool TryDecode(string cursor, out DateTime createdAt, out Guid id)
    {
        createdAt = default;
        id = default;

        byte[] bytes;
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw new FormatException(),
            };
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return false;
        }

        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var separator = text.IndexOf('|', StringComparison.Ordinal);

        return separator > 0
            && DateTime.TryParseExact(
                text.AsSpan(0, separator),
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out createdAt)
            && Guid.TryParse(text.AsSpan(separator + 1), out id);
    }
}

/// <summary>
/// The read behind <c>GET /internal/operator/tasks/dead-letter</c>. It runs inside the named
/// org's tenant transaction and calls <c>evidgate.list_dead_letter_tasks</c>, the owner
/// <c>SECURITY DEFINER</c> projection over <c>public.task</c>; the app role holds no
/// <c>SELECT</c> on that table.
/// </summary>
/// <remarks>
/// Called directly from the endpoint rather than through the Mediator C2
/// pipeline, because the operator surface answers to the platform operator rather
/// than a tenant principal; the org is therefore the route's own parameter and the
/// service opens the scope the function reads <c>evidgate.current_org_id()</c> from.
/// The limit is bounded here (default 50, max 200) because a resource bound
/// belongs with the query, not with whichever caller passes it.
/// </remarks>
public sealed class ListDeadLetterTasksQuery
{
    /// <summary>Default when <c>limit</c> is absent.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Maximum <c>limit</c>.</summary>
    public const int MaxLimit = 200;

    /// <summary>
    /// One page of the org's dead letters. The function's parameters are the keyset
    /// itself — both cursor halves null for the first page — so one statement serves
    /// both pages and the ordering stays inside the definer.
    /// </summary>
    private const string PageSql = """
        select id as "Id",
               org_id as "OrgId",
               queue as "Queue",
               task_type as "TaskType",
               idempotency_key as "IdempotencyKey",
               attempts as "Attempts",
               max_attempts as "MaxAttempts",
               last_error as "LastError",
               created_at as "CreatedAt"
          from evidgate.list_dead_letter_tasks(@queue, @cursor_created_at, @cursor_id, @limit)
        """;

    private readonly ITenantScopeFactory _scopes;

    /// <summary>Creates the query over the tenant scope factory.</summary>
    public ListDeadLetterTasksQuery(ITenantScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        _scopes = scopes;
    }

    /// <summary>
    /// Returns the page after <paramref name="cursor"/> (or the first page when
    /// null/empty) of one org's dead-lettered rows, optionally of one queue.
    /// </summary>
    /// <param name="orgId">The org the operator route names; the scope the function reads the org from.</param>
    /// <param name="queue">Queue filter, or null for all queues.</param>
    /// <param name="cursor">The previous page's <c>next_cursor</c>, or null.</param>
    /// <param name="limit">Requested page size; clamped to 1..<see cref="MaxLimit"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="FormatException"><paramref name="cursor"/> is not a cursor this endpoint produced.</exception>
    public async Task<DeadLetterTaskPage> ExecuteAsync(
        Guid orgId,
        string? queue,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var bounded = Math.Clamp(limit, 1, MaxLimit);

        DateTime? cursorCreatedAt = null;
        Guid? cursorId = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!DeadLetterTaskCursor.TryDecode(cursor, out var decodedCreatedAt, out var decodedId))
            {
                throw new FormatException("the cursor is not a dead-letter continuation this endpoint produced");
            }

            cursorCreatedAt = decodedCreatedAt.ToUniversalTime();
            cursorId = decodedId;
        }

        await using var scope = await _scopes.BeginAsync(orgId, null, true, cancellationToken).ConfigureAwait(false);

        var rows = await scope.Connection.QueryAsync<DeadLetterTaskItem>(new CommandDefinition(
            PageSql,
            new
            {
                queue,
                cursor_created_at = cursorCreatedAt,
                cursor_id = cursorId,
                limit = bounded + 1,
            },
            scope.Transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        var page = rows as IReadOnlyList<DeadLetterTaskItem> ?? [.. rows];

        // The +1 fetch answers "is there more?" without a count; the extra row is
        // the next page's first item, not part of this one — the cursor names the
        // last RETURNED row, so trimming must use the returned page, not the fetch.
        string? nextCursor = null;
        if (page.Count > bounded)
        {
            var last = page[bounded - 1];
            nextCursor = DeadLetterTaskCursor.Encode(last.CreatedAt, last.Id);
            page = page.Take(bounded).ToArray();
        }

        return new DeadLetterTaskPage(page, nextCursor);
    }
}
