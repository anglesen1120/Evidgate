using System.Data;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// A <c>text[]</c> parameter for a Dapper command, typed explicitly.
/// </summary>
/// <remarks>
/// Dapper cannot send a <c>string[]</c> here: the tenant read path registers a
/// read-only <c>string[]</c> type handler (M1W3-02 — writes are EF Core's job), so
/// Dapper's type-handler registry refuses to write an array. Dapper's custom
/// parameter hook runs before that registry, which keeps the claim statement
/// parameterised (00 §5.7 S1) with the array's PostgreSQL type stated by the
/// caller rather than guessed from the CLR type.
/// </remarks>
internal sealed class TextArrayParameter : SqlMapper.ICustomQueryParameter
{
    private readonly string[] _values;

    /// <summary>Wraps the values of one <c>text[]</c> parameter.</summary>
    internal TextArrayParameter(string[] values)
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
        parameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text;
        parameter.Value = _values;

        command.Parameters.Add(parameter);
    }
}
