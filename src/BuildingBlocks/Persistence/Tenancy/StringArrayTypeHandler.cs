using System.Data;
using System.Globalization;
using Dapper;

namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// Dapper type handler for a PostgreSQL <c>text[]</c> column (M1W3-02).
/// <c>organization.allowed_email_domains</c> is an array column, and Npgsql reports
/// it as <see cref="Array"/> — not as the <c>string[]</c> the DTO declares — so
/// without a registered handler Dapper refuses the positional-record constructor of
/// <c>OrganizationDto</c> outright, exactly as it does for the <c>timestamptz</c>
/// timestamps (see <see cref="UtcDateTimeOffsetTypeHandler"/>).
/// </summary>
/// <remarks>
/// The value Npgsql returns is already a <c>string[]</c>; the handler exists so that
/// Dapper selects the constructor and casts the boxed array instead of falling back
/// to <c>Convert.ChangeType</c>, which has no conversion for an array. The write side
/// never goes through Dapper (writes are EF Core, 00 §5.5 C4/C5), so
/// <see cref="SetValue"/> is deliberately left to throw.
/// </remarks>
internal sealed class StringArrayTypeHandler : SqlMapper.TypeHandler<string[]>
{
    /// <inheritdoc />
    public override string[] Parse(object value) => value switch
    {
        string[] text => text,
        Array array => [.. array.Cast<object>().Select(Format)],
        _ => throw new InvalidCastException(
            $"Cannot read {value?.GetType().FullName ?? "null"} as a text array."),
    };

    /// <inheritdoc />
    public override void SetValue(IDbDataParameter parameter, string[]? value) =>
        throw new NotSupportedException(
            "The tenant Dapper path is read-only: writes go through the EF Core write model (00 §5.5 C4/C5).");

    private static string Format(object? item) =>
        item is null or DBNull ? string.Empty : Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty;
}
