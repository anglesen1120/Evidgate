using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Application;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// The rate card's SQL: one statement writes a whole card, and one indexed read answers a lookup. An org
/// card is written on the caller's tenant transaction — row security refuses another org and the unique index refuses a
/// version already loaded — while the global card has no org to scope it to, so it goes through the security-definer
/// function and the store owns that transaction because the definer refuses to run inside a tenant one.
/// <para>
/// A refusal must not poison the caller's unit of work, so the org path wraps its statement in a savepoint and rolls
/// back to it: the card wrote nothing, and the caller's transaction stays usable. Money travels as Npgsql
/// <c>numeric</c> arrays and is read back with <see cref="NpgsqlDataReader.GetDecimal"/>, never through a binary
/// floating-point type.
/// </para>
/// </summary>
internal sealed class UsagePriceStore(
    ITenantScopeAccessor scopes,
    NpgsqlDataSource dataSource)
{
    private const string Savepoint = "usage_price_load";
    private const string CardKeyConstraint = "usage_price_card_key";
    private const string OverlapConstraint = "usage_price_no_overlap";

    /// <summary>
    /// One card, one statement, on the caller's tenant transaction. <c>org_id</c> comes from the scope, never from the
    /// card, so the insert policy of <c>usage_price</c> is satisfied by construction and cannot be aimed elsewhere.
    /// </summary>
    private const string OrgInsertSql = """
        insert into public.usage_price (
            id, org_id, metric, unit_price, currency, effective_from, effective_to, included_quota,
            rate_card_version)
        select
            x.id, @org_id, x.metric, x.unit_price, x.currency, x.effective_from, x.effective_to,
            x.included_quota, @rate_card_version
        from unnest(
            @ids::uuid[],
            @metrics::text[],
            @unit_prices::numeric[],
            @currencies::text[],
            @effective_from::timestamptz[],
            @effective_to::timestamptz[],
            @included_quotas::numeric[])
            with ordinality as x (
                id, metric, unit_price, currency, effective_from, effective_to, included_quota, position)
        order by x.position
        returning id
        """;

    /// <summary>
    /// The global card, one statement and one function call per row: the app role may not insert a row with a null
    /// <c>org_id</c>, so the definer is the only write path and it refuses a tenant transaction.
    /// </summary>
    private const string GlobalInsertSql = """
        select inserted.id
        from unnest(
            @ids::uuid[],
            @metrics::text[],
            @unit_prices::numeric[],
            @currencies::text[],
            @effective_from::timestamptz[],
            @effective_to::timestamptz[],
            @included_quotas::numeric[])
            with ordinality as x (
                id, metric, unit_price, currency, effective_from, effective_to, included_quota, position)
        cross join lateral (
            select evidgate.usage_price_insert_global(
                x.id,
                x.metric,
                x.unit_price,
                x.currency,
                x.effective_from,
                x.effective_to,
                x.included_quota,
                @rate_card_version) as id) as inserted
        order by x.position
        """;

    /// <summary>
    /// The lookup: the caller's own override wins over the global card, and the range is half-open
    /// (<c>effective_from &lt;= at &lt; effective_to</c>), so the instant one range ends belongs to the next. Row
    /// security has already restricted the table to the caller's org and the global rows, but the predicate repeats it
    /// so the plan is the same whatever the role's policies say.
    /// </summary>
    private const string ResolveSql = """
        select id, org_id, unit_price, currency, effective_from, effective_to, included_quota, rate_card_version
        from public.usage_price
        where metric = @metric
          and (org_id = @org_id or org_id is null)
          and effective_from <= @at
          and (effective_to is null or @at < effective_to)
        order by org_id nulls last, effective_from desc
        limit 1
        """;

    /// <summary>Writes <paramref name="card"/> as the org's override on the caller's tenant transaction.</summary>
    /// <param name="orgId">The open scope's tenant (never a value from the card).</param>
    /// <param name="card">The validated card.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<UsagePriceLoadResult> InsertOrgAsync(
        Guid orgId,
        UsagePriceCard card,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(card);

        var scope = scopes.Require();
        await ExecuteAsync(scope, "savepoint " + Savepoint, cancellationToken).ConfigureAwait(false);

        try
        {
            var (built, generated, cardIndexes) = CreateCommand(scope.Connection, scope.Transaction, card, orgId);
            await using var command = built;
            var written = await ReadIdsAsync(command, cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(scope, "release savepoint " + Savepoint, cancellationToken).ConfigureAwait(false);
            return UsagePriceLoadResult.Loaded(InCardOrder(written, generated, cardIndexes));
        }
        catch (PostgresException error) when (MapRefusal(error) is { } reason)
        {
            // The rollback keeps the caller's transaction usable: a refused card wrote nothing, and the caller can
            // correct it and carry on inside the same unit of work.
            await ExecuteAsync(scope, "rollback to savepoint " + Savepoint, cancellationToken).ConfigureAwait(false);
            return UsagePriceLoadResult.Refused([new UsagePriceViolation("/prices", reason)]);
        }
    }

    /// <summary>Writes <paramref name="card"/> as the global rate card, in a transaction this call owns and commits.</summary>
    /// <param name="card">The validated card.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<UsagePriceLoadResult> InsertGlobalAsync(
        UsagePriceCard card,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(card);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var (built, generated, cardIndexes) = CreateCommand(connection, transaction, card, orgId: null);
            await using var command = built;
            var written = await ReadIdsAsync(command, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return UsagePriceLoadResult.Loaded(InCardOrder(written, generated, cardIndexes));
        }
        catch (PostgresException error) when (MapRefusal(error) is { } reason)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return UsagePriceLoadResult.Refused([new UsagePriceViolation("/prices", reason)]);
        }
    }

    /// <summary>The price of <paramref name="metric"/> in force at <paramref name="at"/> for the open scope's tenant.</summary>
    /// <param name="metric">The metric being priced.</param>
    /// <param name="at">The instant the metered work happened.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<ResolvedUsagePrice?> ResolveAsync(
        UsageMetric metric,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var scope = scopes.Require();

        await using var command = scope.Connection.CreateCommand();
        command.CommandText = ResolveSql;
        command.Transaction = scope.Transaction;
        command.Parameters.Add(new NpgsqlParameter("metric", NpgsqlDbType.Text)
        {
            Value = UsageMetricWire.ToWireValue(metric),
        });
        command.Parameters.Add(new NpgsqlParameter("org_id", NpgsqlDbType.Uuid) { Value = scope.OrgId });
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at.UtcDateTime });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var orgId = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
            ? (Guid?)null
            : reader.GetGuid(1);
        var effectiveFrom = ReadInstant(reader, 4);
        var effectiveTo = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
            ? (DateTimeOffset?)null
            : ReadInstant(reader, 5);
        var includedQuota = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
            ? (decimal?)null
            : reader.GetDecimal(6);

        return new ResolvedUsagePrice(
            reader.GetGuid(0),
            orgId,
            metric,
            reader.GetDecimal(2),
            reader.GetString(3),
            effectiveFrom,
            effectiveTo,
            includedQuota,
            reader.GetString(7));
    }

    private static (NpgsqlCommand Command, Guid[] Generated, int[] CardIndexes) CreateCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UsagePriceCard card,
        Guid? orgId)
    {
        var count = card.Rows.Count;

        // The overlap trigger takes one advisory lock per (scope, metric) in the order the rows arrive, so two cards
        // holding the same metrics in different orders would wait on each other. Writing every card in metric order
        // gives all loads one lock order; the returned ids are put back in the card's order by the caller.
        var order = Enumerable.Range(0, count)
            .OrderBy(slot => (int)card.Rows[slot].Metric)
            .ToArray();

        var ids = new Guid[count];
        var metrics = new string[count];
        var unitPrices = new decimal[count];
        var currencies = new string[count];
        var effectiveFrom = new DateTime[count];
        var effectiveTo = new DateTime?[count];
        var includedQuotas = new decimal?[count];

        for (var slot = 0; slot < count; slot++)
        {
            var row = card.Rows[order[slot]];
            ids[slot] = Guid.CreateVersion7();
            metrics[slot] = UsageMetricWire.ToWireValue(row.Metric);
            unitPrices[slot] = row.UnitPrice;
            currencies[slot] = row.Currency;
            effectiveFrom[slot] = row.EffectiveFrom.UtcDateTime;
            effectiveTo[slot] = row.EffectiveTo?.UtcDateTime;
            includedQuotas[slot] = row.IncludedQuota;
        }

        var command = connection.CreateCommand();
        command.CommandText = orgId is null ? GlobalInsertSql : OrgInsertSql;
        command.Transaction = transaction;
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
        command.Parameters.Add(new NpgsqlParameter("metrics", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = metrics });
        command.Parameters.Add(new NpgsqlParameter("unit_prices", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = unitPrices });
        command.Parameters.Add(new NpgsqlParameter("currencies", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = currencies });
        command.Parameters.Add(new NpgsqlParameter("effective_from", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = effectiveFrom });
        command.Parameters.Add(new NpgsqlParameter("effective_to", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = effectiveTo });
        command.Parameters.Add(new NpgsqlParameter("included_quotas", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = includedQuotas });
        command.Parameters.Add(new NpgsqlParameter("rate_card_version", NpgsqlDbType.Text) { Value = card.RateCardVersion });

        if (orgId is { } org)
        {
            command.Parameters.Add(new NpgsqlParameter("org_id", NpgsqlDbType.Uuid) { Value = org });
        }

        return (command, ids, order);
    }

    /// <summary>
    /// Puts the ids the statement returned back in the card's own order. The rows were written in metric order, and
    /// <c>RETURNING</c> does not promise an order of its own, so each id is matched to the row that generated it
    /// instead of trusting the result's sequence.
    /// </summary>
    private static Guid[] InCardOrder(IReadOnlyList<Guid> written, Guid[] generated, int[] cardIndexes)
    {
        if (written.Count != generated.Length)
        {
            throw new InvalidOperationException(
                "the card load returned " + written.Count + " rows for a card of " + generated.Length);
        }

        var cardIndexById = new Dictionary<Guid, int>(generated.Length);
        for (var slot = 0; slot < generated.Length; slot++)
        {
            cardIndexById[generated[slot]] = cardIndexes[slot];
        }

        var ids = new Guid[written.Count];
        foreach (var id in written)
        {
            ids[cardIndexById[id]] = id;
        }

        return ids;
    }

    private static async Task<IReadOnlyList<Guid>> ReadIdsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static Task<int> ExecuteAsync(ITenantScope scope, string sql, CancellationToken cancellationToken)
    {
        using var command = scope.Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = scope.Transaction;

        return command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset ReadInstant(NpgsqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(ordinal), DateTimeKind.Utc), TimeSpan.Zero);

    /// <summary>
    /// Maps the database's refusal to the card's stable reason: the card-version index is the duplicate arbiter, and the
    /// overlap trigger raises either an exclusion violation or a check violation named <c>usage_price_no_overlap</c>.
    /// Anything else is a bug the loader must not hide, so it propagates.
    /// </summary>
    private static string? MapRefusal(PostgresException error) => error switch
    {
        // Named, not "any unique violation": an id collision is a bug, and it must surface as one.
        { SqlState: PostgresErrorCodes.UniqueViolation } when IsConstraint(error, CardKeyConstraint) =>
            UsagePriceRefusalReasons.DuplicateVersion,
        { SqlState: PostgresErrorCodes.ExclusionViolation } when IsConstraint(error, OverlapConstraint) =>
            UsagePriceRefusalReasons.OverlappingRange,
        { SqlState: PostgresErrorCodes.CheckViolation } when IsConstraint(error, OverlapConstraint) =>
            UsagePriceRefusalReasons.OverlappingRange,
        _ => null,
    };

    private static bool IsConstraint(PostgresException error, string name) =>
        string.Equals(error.ConstraintName, name, StringComparison.Ordinal);
}
