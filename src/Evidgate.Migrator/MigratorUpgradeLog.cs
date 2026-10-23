using System.Globalization;
using DbUp.Engine.Output;

namespace Evidgate.Migrator;

/// <summary>
/// DbUp's log, adapted to the Migrator's own transcript: everything DbUp says
/// goes to the console <em>and</em> into the run's message list, with secrets
/// masked (00 §5.7 S3). Every line is prefixed so an operator can tell DbUp
/// output from Migrator output.
/// </summary>
internal sealed class MigratorUpgradeLog : IUpgradeLog
{
    private const string Prefix = "dbup";

    private readonly Action<string> _sink;
    private readonly string? _secret;

    internal MigratorUpgradeLog(Action<string> sink, string? secret)
    {
        _sink = sink;
        _secret = secret;
    }

    public void LogTrace(string format, params object[] args)
    {
        // Dropped: DbUp traces are framework detail (connection plumbing) and
        // would bury the one line an operator reads in a deploy log.
    }

    public void LogDebug(string format, params object[] args)
    {
        // Dropped for the same reason as LogTrace.
    }

    public void LogInformation(string format, params object[] args) => Write("info", format, args);

    public void LogWarning(string format, params object[] args) => Write("warn", format, args);

    public void LogError(string format, params object[] args) => Write("error", format, args);

    /// <summary>
    /// DbUp reports a failure as a context line plus the exception; the reason
    /// itself (the Postgres error message) is what an operator acts on, so it is
    /// always printed once, redacted, when the format does not already carry it.
    /// </summary>
    public void LogError(Exception exception, string format, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var formatted = Format(format, args);
        Write("error", formatted);

        var cause = FirstLine(exception.Message);
        if (cause.Length > 0 && !formatted.Contains(cause, StringComparison.Ordinal))
        {
            Write("error", cause);
        }
    }

    /// <summary>Masks secrets in an arbitrary message (used for DbUp's failure text).</summary>
    internal string Redact(string? text) => SecretRedactor.Redact(text, _secret);

    private static string Format(string format, object[] args)
    {
        if (args.Length == 0)
        {
            return format;
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
        catch (FormatException)
        {
            // DbUp 6 follows Microsoft.Extensions.Logging, whose templates use
            // named placeholders ({ScriptName}); a positional format would throw.
            // Falling back keeps a good run from turning into exit 3.
            return format + " " + string.Join(", ", args);
        }
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return (end < 0 ? text : text[..end]).Trim();
    }

    private void Write(string level, string format, object[] args) =>
        Write(level, Format(format, args));

    private void Write(string level, string message)
    {
        message = SecretRedactor.Redact(message, _secret);

        // DbUp hands exceptions to the log with their stack traces attached; the
        // Migrator's own summary line already names the script and the message,
        // so only the first line of each message is kept.
        foreach (var candidate in message.Split('\n'))
        {
            var line = candidate.TrimEnd('\r').Trim();
            if (line.Length > 0)
            {
                _sink($"[{Prefix}:{level}] {line}");
                return;
            }
        }

        _sink($"[{Prefix}:{level}]");
    }
}
