using System.Globalization;
using Dapper;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// One dead-lettered task row as the operator list serves it (01.5.1 §5, response
/// <c>items</c> entry). The mechanism columns that an operator does not need
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
/// One page of the operator dead-letter list (00 §9.1): the items plus the
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
/// <c>ListDeadLetterTasksQuery</c> of 01.5.1 §5/T4.2 — the read behind
/// <c>GET /internal/operator/tasks/dead-letter</c>. Dapper over the app-role
/// <see cref="NpgsqlDataSource"/> with the keyset of
/// <see cref="TaskQueueSql.DeadLetterFirstPage"/>/<see cref="TaskQueueSql.DeadLetterNextPage"/>.
/// </summary>
/// <remarks>
/// Called directly from the endpoint rather than through the Mediator C2
/// pipeline: an operator route carries no tenant context, so a
/// <c>Result</c>-contract query would trip the tenant stages' fail-closed
/// Require() — §5 says "routes are outside /v1" precisely so those stages do not
/// apply. The limit is bounded here (00 §9.1: default 50, max 200) because a
/// resource bound belongs with the query, not with whichever caller passes it.
/// </remarks>
public sealed class ListDeadLetterTasksQuery
{
    /// <summary>00 §9.1 default when <c>limit</c> is absent.</summary>
    public const int DefaultLimit = 50;

    /// <summary>00 §9.1 maximum <c>limit</c>.</summary>
    public const int MaxLimit = 200;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the query over the registered app-role data source.</summary>
    public ListDeadLetterTasksQuery(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <summary>
    /// Returns the page after <paramref name="cursor"/> (or the first page when
    /// null/empty) of dead-lettered rows, optionally of one queue.
    /// </summary>
    /// <param name="queue">Queue filter, or null for all queues.</param>
    /// <param name="cursor">The previous page's <c>next_cursor</c>, or null.</param>
    /// <param name="limit">Requested page size; clamped to 1..<see cref="MaxLimit"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="FormatException"><paramref name="cursor"/> is not a cursor this endpoint produced.</exception>
    public async Task<DeadLetterTaskPage> ExecuteAsync(
        string? queue,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var bounded = Math.Clamp(limit, 1, MaxLimit);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<DeadLetterTaskItem> rows;
        if (string.IsNullOrEmpty(cursor))
        {
            rows = await connection.QueryAsync<DeadLetterTaskItem>(new CommandDefinition(
                TaskQueueSql.DeadLetterFirstPage,
                new { queue, limit = bounded + 1 },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        else
        {
            if (!DeadLetterTaskCursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
            {
                throw new FormatException("the cursor is not a dead-letter continuation this endpoint produced");
            }

            rows = await connection.QueryAsync<DeadLetterTaskItem>(new CommandDefinition(
                TaskQueueSql.DeadLetterNextPage,
                new
                {
                    queue,
                    cursor_created_at = cursorCreatedAt.ToUniversalTime(),
                    cursor_id = cursorId,
                    limit = bounded + 1,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

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
