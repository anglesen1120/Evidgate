using Npgsql;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The ledger's seed helpers: rows the reads answer from, written as the owner so a case can pin the keyset's instant
/// and write a second org's rows without a tenant transaction.
/// </summary>
/// <remarks>
/// A row that carries <c>original_period</c> is written with the insert guard lifted, because the guard decides that
/// marker itself and refuses a writer that supplies one. Lifting it for the seed is the only way to represent a row
/// the guard re-dated earlier; it is restored immediately afterwards.
/// </remarks>
internal sealed partial class UsageReadsWorld
{
    /// <summary>Inserts the given ledger rows, one parameterised statement each.</summary>
    /// <param name="rows">The rows to insert.</param>
    internal async Task SeedLedgerAsync(params LedgerSeed[] rows)
    {
        var suppliesProvenance = rows.Any(row => row.OriginalPeriod is not null);
        if (suppliesProvenance)
        {
            await DisableIssuedPeriodGuardAsync();
        }

        try
        {
            await InsertLedgerRowsAsync(rows);
        }
        finally
        {
            if (suppliesProvenance)
            {
                await EnableIssuedPeriodGuardAsync();
            }
        }
    }

    private async Task InsertLedgerRowsAsync(IReadOnlyList<LedgerSeed> rows)
    {
        await using var connection = await OpenOwnerAsync();

        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                insert into public.usage_ledger_entry
                    (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at, created_at,
                     adjustment_of, reason, price_ref, amount, currency, original_period)
                values
                    (@id, @org_id, @project_id, @metric, @quantity, @unit, @period, @billing_key, @occurred_at,
                     @created_at, @adjustment_of, @reason, @price_ref, @amount, @currency, @original_period)
                """;
            command.Parameters.Add(new NpgsqlParameter("id", row.Id));
            command.Parameters.Add(new NpgsqlParameter("org_id", row.Org ?? Org));
            command.Parameters.Add(new NpgsqlParameter("project_id", (object?)row.ProjectId ?? DBNull.Value));
            command.Parameters.Add(new NpgsqlParameter("metric", row.Metric));
            command.Parameters.Add(new NpgsqlParameter("quantity", row.Quantity));
            command.Parameters.Add(new NpgsqlParameter("unit", row.Unit));
            command.Parameters.Add(new NpgsqlParameter("period", row.Period));
            command.Parameters.Add(new NpgsqlParameter("billing_key", row.BillingKey));
            command.Parameters.Add(new NpgsqlParameter("occurred_at", row.OccurredAt ?? row.CreatedAt));
            command.Parameters.Add(new NpgsqlParameter("created_at", row.CreatedAt));
            command.Parameters.Add(new NpgsqlParameter("adjustment_of", (object?)row.AdjustmentOf ?? DBNull.Value));
            command.Parameters.Add(new NpgsqlParameter("reason", (object?)row.Reason ?? DBNull.Value));
            command.Parameters.Add(new NpgsqlParameter("price_ref", (object?)row.PriceRef ?? DBNull.Value));
            command.Parameters.Add(new NpgsqlParameter("amount", (object?)row.Amount ?? DBNull.Value));
            command.Parameters.Add(new NpgsqlParameter("currency", (object?)row.Currency ?? DBNull.Value));
            command.Parameters.Add(new NpgsqlParameter("original_period", (object?)row.OriginalPeriod ?? DBNull.Value));

            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }
}
