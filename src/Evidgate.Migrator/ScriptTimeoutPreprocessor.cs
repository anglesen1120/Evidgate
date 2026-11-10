using System.Globalization;
using DbUp.Engine;

namespace Evidgate.Migrator;

/// <summary>
/// Puts <c>SET LOCAL lock_timeout</c> and <c>SET LOCAL statement_timeout</c> in front of every
/// script, so both live exactly as long as the script's own transaction and need no reset.
/// </summary>
/// <remarks>
/// The prefix stays on the script's first line, which keeps the line numbers PostgreSQL reports
/// equal to the file's. It runs before the script body, so a script that sets its own value with
/// <c>set local</c> overrides it.
/// </remarks>
internal sealed class ScriptTimeoutPreprocessor(MigratorOptions options) : IScriptPreprocessor
{
    private readonly string _prefix = string.Create(
        CultureInfo.InvariantCulture,
        $"set local lock_timeout = '{options.LockTimeoutSeconds * 1000L}ms'; set local statement_timeout = '{options.StatementTimeoutSeconds * 1000L}ms'; ");

    /// <summary>Returns the script with the timeout statements prepended.</summary>
    public string Process(string contents) => _prefix + contents;
}
