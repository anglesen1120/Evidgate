using System.Data;
using System.Globalization;
using Dapper;

namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// Dapper type handler bridging the one representation gap of the read path
/// (M1W3-02): Npgsql reports <c>timestamp with time zone</c> as a
/// <see cref="DateTime"/> with <see cref="DateTimeKind.Utc"/>, while every DTO of
/// the platform declares its timestamps as <see cref="DateTimeOffset"/>
/// (00 §5.4.4/§7 — what the API serializes). Without a handler Dapper cannot even
/// select the positional-record constructor: a constructor parameter is accepted
/// only when its type equals the reader's field type or has a registered type
/// handler.
/// </summary>
/// <remarks>
/// Registered once by <c>AddEvidgateTenancy</c>, which is also where the
/// underscore name matching is switched on. The value is always interpreted as
/// UTC — the column is <c>timestamptz</c>, so Npgsql has already normalized it —
/// and an unspecified kind is therefore read as UTC rather than as local time.
/// The write side never goes through Dapper (writes are EF Core, 00 §5.5 C4/C5),
/// so <see cref="SetValue"/> is deliberately left to throw.
/// </remarks>
internal sealed class UtcDateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    /// <inheritdoc />
    public override DateTimeOffset Parse(object value) => value switch
    {
        DateTimeOffset offset => offset,
        DateTime utc => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)),
        string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
        _ => throw new InvalidCastException(
            $"Cannot read {value?.GetType().FullName ?? "null"} as a UTC DateTimeOffset."),
    };

    /// <inheritdoc />
    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) =>
        throw new NotSupportedException(
            "The tenant Dapper path is read-only: writes go through the EF Core write model (00 §5.5 C4/C5).");
}
