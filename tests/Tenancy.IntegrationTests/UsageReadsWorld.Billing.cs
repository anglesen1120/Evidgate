using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The invoice side of the billing operator's world: the worksheet and statement routes of this org, the priced
/// bulk seed a bounded read needs, and the statement rows as the operator's own client can never see them.
/// </summary>
/// <remarks>
/// The statement table is read as the owner here for the same reason the ledger seeds are: these tests assert what
/// was frozen, and a read through the route would prove only what the route says today — a late change to the
/// period would be invisible to it.
/// </remarks>
internal sealed partial class UsageReadsWorld
{
    /// <summary>The url of the invoice worksheet of this world's org.</summary>
    /// <param name="query">The query string, without a leading <c>?</c>.</param>
    internal string WorksheetUrl(string query = "") =>
        $"/internal/operator/orgs/{Org:D}/invoice-worksheet{(query.Length == 0 ? string.Empty : "?" + query)}";

    /// <summary>The url of one org's invoice worksheet.</summary>
    /// <param name="org">The org the route names.</param>
    /// <param name="query">The query string, without a leading <c>?</c>.</param>
    internal static string WorksheetUrlFor(Guid org, string query = "") =>
        $"/internal/operator/orgs/{org:D}/invoice-worksheet{(query.Length == 0 ? string.Empty : "?" + query)}";

    /// <summary>The url of the statements of this world's org, or of another org the test names.</summary>
    /// <param name="org">The org the route names, or null for this world's.</param>
    internal string StatementUrl(Guid? org = null) =>
        $"/internal/operator/billing/orgs/{(org ?? Org):D}/statements";

    /// <summary>Sends a GET and hands back the body as text, which is what a byte-for-byte comparison needs.</summary>
    /// <param name="url">The route.</param>
    internal async Task<(HttpStatusCode Status, string Text)> GetTextAsync(string url)
    {
        using var response = await Client.GetAsync(url, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        return (response.StatusCode, text);
    }

    /// <summary>Posts a statement body: the period, the total the operator submits and its currency.</summary>
    /// <param name="period">The period to issue.</param>
    /// <param name="totalAmount">The submitted total.</param>
    /// <param name="currency">The submitted currency.</param>
    /// <param name="org">The org the route names, or null for this world's.</param>
    internal Task<ApiAnswer> PostStatementAsync(
        string? period,
        decimal? totalAmount,
        string? currency,
        Guid? org = null) =>
        Client.SendAsync(
            HttpMethod.Post,
            StatementUrl(org),
            _ => { },
            new { period, total_amount = totalAmount, currency });

    /// <summary>Posts a body of the test's own making, optionally chunked so no header declares its size.</summary>
    /// <param name="json">The body.</param>
    /// <param name="chunked">True sends it chunked, which no declared length bounds.</param>
    /// <param name="org">The org the route names, or null for this world's.</param>
    internal async Task<ApiAnswer> PostStatementRawAsync(string json, bool chunked = false, Guid? org = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, StatementUrl(org))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (chunked)
        {
            request.Headers.TransferEncodingChunked = true;
        }

        using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>
    /// Inserts many priced ledger rows of one period in one statement: the shape a period takes past the worksheet's
    /// row bound, where every row is billable and the only thing that can refuse the issue is the cut itself.
    /// </summary>
    /// <param name="count">Rows to insert.</param>
    /// <param name="period">Their period.</param>
    /// <param name="metric">Their metric.</param>
    /// <param name="quantityEach">The quantity each row carries.</param>
    /// <param name="amountEach">The amount each row carries.</param>
    /// <param name="currency">The currency each amount is stated in.</param>
    internal async Task SeedManyPricedLedgerRowsAsync(
        int count,
        string period,
        string metric,
        decimal quantityEach,
        decimal amountEach,
        string currency)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into public.usage_ledger_entry
                (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at, amount, currency)
            select gen_random_uuid(), @org_id, @project_id, @metric, @quantity, 'bulk', @period,
                   'bulk-priced:' || @period || ':' || @metric || ':' || n, now(), @amount, @currency
              from generate_series(1, @count) as n
            """;
        command.Parameters.Add(new NpgsqlParameter("org_id", Org));
        command.Parameters.Add(new NpgsqlParameter("project_id", Project));
        command.Parameters.Add(new NpgsqlParameter("metric", metric));
        command.Parameters.Add(new NpgsqlParameter("quantity", quantityEach));
        command.Parameters.Add(new NpgsqlParameter("period", period));
        command.Parameters.Add(new NpgsqlParameter("amount", amountEach));
        command.Parameters.Add(new NpgsqlParameter("currency", currency));
        command.Parameters.Add(new NpgsqlParameter("count", count));

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts many corrections of one period, each naming <paramref name="adjustmentOf"/> and carrying the same
    /// reason: the shape a period takes when its corrections alone fill a worksheet.
    /// </summary>
    /// <param name="count">Corrections to insert.</param>
    /// <param name="period">Their period.</param>
    /// <param name="metric">Their metric.</param>
    /// <param name="adjustmentOf">The row every correction names.</param>
    /// <param name="reason">The reason each correction carries.</param>
    /// <param name="currency">The currency each amount is stated in.</param>
    internal async Task SeedManyCorrectionsAsync(
        int count,
        string period,
        string metric,
        Guid adjustmentOf,
        string reason,
        string currency)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into public.usage_ledger_entry
                (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at,
                 adjustment_of, reason, amount, currency)
            select gen_random_uuid(), @org_id, @project_id, @metric, -1, 'bulk', @period,
                   'bulk-correction:' || @period || ':' || @metric || ':' || n, now(),
                   @adjustment_of, @reason, -1, @currency
              from generate_series(1, @count) as n
            """;
        command.Parameters.Add(new NpgsqlParameter("org_id", Org));
        command.Parameters.Add(new NpgsqlParameter("project_id", Project));
        command.Parameters.Add(new NpgsqlParameter("metric", metric));
        command.Parameters.Add(new NpgsqlParameter("period", period));
        command.Parameters.Add(new NpgsqlParameter("adjustment_of", adjustmentOf));
        command.Parameters.Add(new NpgsqlParameter("reason", reason));
        command.Parameters.Add(new NpgsqlParameter("currency", currency));
        command.Parameters.Add(new NpgsqlParameter("count", count));

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>How many ledger rows this world's org holds for one period.</summary>
    /// <param name="period">The period.</param>
    internal async Task<long> LedgerRowCountAsync(string period)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "select count(*) from public.usage_ledger_entry where org_id = @org_id and period = @period";
        command.Parameters.Add(new NpgsqlParameter("org_id", Org));
        command.Parameters.Add(new NpgsqlParameter("period", period));

        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// Writes a statement row as the owner: the document an earlier issue of the period left, with a content hash the
    /// test chooses so a re-issue can be made to disagree with it.
    /// </summary>
    /// <param name="period">The period the statement bills.</param>
    /// <param name="total">The frozen total.</param>
    /// <param name="contentHash">The frozen document address, 64 lowercase hex characters.</param>
    internal async Task SeedStatementAsync(string period, decimal total, string contentHash)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into public.billing_statement
                (id, org_id, period, kind, total_amount, currency, line_count, content_hash, worksheet_json,
                 issued_by, issued_at)
            values (gen_random_uuid(), @org_id, @period, 'invoice', @total, 'USD', 1, @content_hash,
                    '{"period":"issued"}'::jsonb, @issued_by, now())
            """;
        command.Parameters.Add(new NpgsqlParameter("org_id", Org));
        command.Parameters.Add(new NpgsqlParameter("period", period));
        command.Parameters.Add(new NpgsqlParameter("total", total));
        command.Parameters.Add(new NpgsqlParameter("content_hash", contentHash));
        command.Parameters.Add(new NpgsqlParameter("issued_by", OperatorId));

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Lifts the ledger's issued-period guard for the caller. A row that reached a period before the guard existed is
    /// the only way to represent a ledger that moved after it was billed, which the worksheet read must still report.
    /// </summary>
    internal async Task DisableIssuedPeriodGuardAsync()
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            set lock_timeout = '10s';
            alter table public.usage_ledger_entry disable trigger usage_ledger_entry_period_issued;
            """;

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Restores the guard, so a later write to an issued period is refused again.</summary>
    internal async Task EnableIssuedPeriodGuardAsync()
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            set lock_timeout = '10s';
            alter table public.usage_ledger_entry enable trigger usage_ledger_entry_period_issued;
            """;

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Starts a producer transaction that has written one more priced row of <paramref name="period"/> and has not
    /// committed it: the row a freeze of that period must not bill.
    /// </summary>
    /// <param name="period">The period the row belongs to.</param>
    /// <param name="amount">The amount it bills.</param>
    internal async Task<LateLedgerRowWriter> BeginLateLedgerRowAsync(string period, decimal amount) =>
        await LateLedgerRowWriter
            .StartAsync(await OpenOwnerAsync(), await OpenOwnerAsync(), Org, Project, period, amount)
            .ConfigureAwait(false);

    /// <summary>How many statements this world's org holds for one period.</summary>
    /// <param name="period">The period.</param>
    internal async Task<long> StatementCountAsync(string period)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "select count(*) from public.billing_statement where org_id = @org_id and period = @period";
        command.Parameters.Add(new NpgsqlParameter("org_id", Org));
        command.Parameters.Add(new NpgsqlParameter("period", period));

        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// The stored statement of one period as
    /// <c>id|kind|total_amount|currency|line_count|content_hash|issued_by|worksheet_json</c>, or null without one.
    /// The totals come back as text so a test compares the document, not a parsed approximation of it.
    /// </summary>
    /// <param name="period">The period.</param>
    internal async Task<string?> StatementRowAsync(string period)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id || '|' || kind || '|' || total_amount::text || '|' || currency || '|' || line_count
                   || '|' || content_hash || '|' || issued_by || '|' || worksheet_json
              from public.billing_statement
             where org_id = @org_id and period = @period
            """;
        command.Parameters.Add(new NpgsqlParameter("org_id", Org));
        command.Parameters.Add(new NpgsqlParameter("period", period));

        return (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The quantity of the single adjustment this world's ledger holds for a period, if any.</summary>
    /// <param name="period">The period.</param>
    internal async Task<decimal?> AdjustmentQuantityAsync(string period)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select sum(quantity)
              from public.usage_ledger_entry
             where org_id = @org_id and period = @period and adjustment_of is not null
            """;
        command.Parameters.Add(new NpgsqlParameter("org_id", Org));
        command.Parameters.Add(new NpgsqlParameter("period", period));

        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        return value is DBNull or null ? null : (decimal)value;
    }
}

/// <summary>
/// A producer's open transaction holding one more priced row of a period: the row a statement of that period must not
/// bill. Its insert took the period's shared advisory lock, so a statement insert queues behind it — which is what
/// places the row between the worksheet read and the freeze without a sleep or a trigger.
/// </summary>
internal sealed class LateLedgerRowWriter : IAsyncDisposable
{
    /// <summary>How long a statement insert may take to queue behind the row before the test calls the race off.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly NpgsqlConnection _writer;
    private readonly NpgsqlConnection _watcher;
    private readonly NpgsqlTransaction _transaction;

    private LateLedgerRowWriter(NpgsqlConnection writer, NpgsqlConnection watcher, NpgsqlTransaction transaction)
    {
        _writer = writer;
        _watcher = watcher;
        _transaction = transaction;
    }

    /// <summary>Writes the row inside a transaction of its own and returns that transaction still open.</summary>
    /// <param name="writer">The producer's connection; this writer owns it from here.</param>
    /// <param name="watcher">The connection that reads <c>pg_locks</c> while the statement waits.</param>
    /// <param name="org">The row's tenant.</param>
    /// <param name="project">The row's project.</param>
    /// <param name="period">The period the row belongs to.</param>
    /// <param name="amount">The amount it bills.</param>
    internal static async Task<LateLedgerRowWriter> StartAsync(
        NpgsqlConnection writer,
        NpgsqlConnection watcher,
        Guid org,
        Guid project,
        string period,
        decimal amount)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transaction = await writer.BeginTransactionAsync(cancellationToken);

        await using var command = writer.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into public.usage_ledger_entry
                (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at, amount, currency)
            values (gen_random_uuid(), @org_id, @project_id, 'results', 1, 'results', @period, 'late-ledger-row',
                    now(), @amount, 'USD')
            """;
        command.Parameters.Add(new NpgsqlParameter("org_id", org));
        command.Parameters.Add(new NpgsqlParameter("project_id", project));
        command.Parameters.Add(new NpgsqlParameter("period", period));
        command.Parameters.Add(new NpgsqlParameter("amount", amount));

        await command.ExecuteNonQueryAsync(cancellationToken);

        return new LateLedgerRowWriter(writer, watcher, transaction);
    }

    /// <summary>Waits until a transaction of this database is queued behind the period's advisory lock.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task WaitForStatementAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = _watcher.CreateCommand();
            command.CommandText =
                """
                select count(*) from pg_locks
                 where locktype = 'advisory' and objsubid = 1 and not granted
                   and database = (select oid from pg_database where datname = current_database())
                """;

            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! > 0)
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException("no statement insert queued behind the period's advisory lock in time");
    }

    /// <summary>Commits the row: the freeze's own re-read then sees a period its worksheet does not describe.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task CommitAsync(CancellationToken cancellationToken) => _transaction.CommitAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _writer.DisposeAsync();
        await _watcher.DisposeAsync();
    }
}

