using System.Data;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace Evidgate.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// A <c>uuid[]</c> parameter for a Dapper command, typed explicitly.
/// </summary>
/// <remarks>
/// Mirrors <c>TextArrayParameter</c>: the tenant read path registers a read-only
/// array type handler (M1W3-02 — writes are EF Core's job), so Dapper's
/// type-handler registry refuses to write an array. The custom-parameter hook
/// runs before that registry, which keeps the dispatcher's statements
/// parameterised (00 §5.7 S1) with the array's PostgreSQL type stated by the
/// caller rather than guessed.
/// </remarks>
internal sealed class UuidArrayParameter : SqlMapper.ICustomQueryParameter
{
    private readonly Guid[] _values;

    /// <summary>Wraps the values of one <c>uuid[]</c> parameter.</summary>
    internal UuidArrayParameter(Guid[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values;
    }

    /// <inheritdoc />
    public void AddParameter(IDbCommand command, string name)
    {
        ArgumentNullException.ThrowIfNull(command);

        var parameter = (NpgsqlParameter)command.CreateParameter();
        parameter.ParameterName = name;
        parameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid;
        parameter.Value = _values;

        command.Parameters.Add(parameter);
    }
}
