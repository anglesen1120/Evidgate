using System.Diagnostics;
using DbUp.Engine;

namespace Evidgate.Migrator;

/// <summary>
/// DbUp has no per-script timing hook, so this preprocessor — the last thing
/// DbUp calls before executing a script — records when the current script
/// started; <see cref="SchemaMigrationJournal"/> then reads
/// <see cref="ElapsedMilliseconds"/> when it writes the journal row.
/// </summary>
/// <remarks>
/// The DbUp upgrade loop is single-threaded and calls
/// <see cref="Process"/> exactly once per script, so the single "currently
/// running script" slot is unambiguous.
/// </remarks>
internal sealed class ScriptTimingPreprocessor : IScriptPreprocessor
{
    private long _startedAt;

    /// <summary>Milliseconds since DbUp started the current script; null before the first script.</summary>
    internal int? ElapsedMilliseconds =>
        _startedAt == 0 ? null : (int)Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;

    /// <summary>Records the start of a script and returns its contents unchanged.</summary>
    public string Process(string contents)
    {
        _startedAt = Stopwatch.GetTimestamp();
        return contents;
    }
}
