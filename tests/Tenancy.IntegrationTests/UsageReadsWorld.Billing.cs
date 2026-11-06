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
