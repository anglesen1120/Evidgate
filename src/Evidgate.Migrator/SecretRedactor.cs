using System.Text.RegularExpressions;
using Npgsql;

namespace Evidgate.Migrator;

/// <summary>
/// Keeps secrets out of logs and messages (00 §5.7 S3): the configured password
/// is replaced verbatim, and any <c>password=…</c> pair — the shape a driver
/// echoes back in an exception — is masked even when the secret is unknown.
/// </summary>
internal static class SecretRedactor
{
    private const string Redacted = "<redacted>";

    // A connection-string value is either quoted (and may then contain ';',
    // spaces and a doubled quote) or an unquoted run up to the next ';'.
    private static readonly Regex PasswordPattern = new(
        """(?<key>(?:password|pwd)\s*=\s*)(?<value>"(?:[^"]|"")*"|'(?:[^']|'')*'|[^;\s]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Returns <paramref name="text"/> with every known or recognisable secret masked.</summary>
    internal static string Redact(string? text, string? secret)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // The configured secret goes first: it is the only form that is certain,
        // and a pattern pass alone would mask just the prefix of a value that
        // contains ';' or a quote and leave the rest of the credential visible.
        var redacted = string.IsNullOrEmpty(secret)
            ? text
            : text.Replace(secret, Redacted, StringComparison.Ordinal);

        return PasswordPattern.Replace(redacted, "${key}" + Redacted);
    }

    /// <summary>Extracts the password of a connection string for <see cref="Redact"/>; the value is never logged itself.</summary>
    internal static string? PasswordOf(string connectionString)
    {
        try
        {
            return string.IsNullOrEmpty(connectionString)
                ? null
                : new NpgsqlConnectionStringBuilder(connectionString).Password;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
