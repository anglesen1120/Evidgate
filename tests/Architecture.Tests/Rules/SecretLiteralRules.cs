using System.Text.RegularExpressions;

namespace Architecture.Tests.Rules;

/// <summary>
/// 10.1.1-T2 (AC3): no source file outside the secret seam may spell a secret key
/// as a literal, read a connection string through <c>GetConnectionString</c>, name
/// a secret property as a configuration key, or read the process environment. The
/// rule is pure — it takes the files to scan and the exempt paths — so the test
/// proves it on synthetic bypasses and then runs the same function over the real
/// solution.
/// </summary>
/// <remarks>
/// Every key comparison ignores case: <c>IConfiguration</c> keys are
/// case-insensitive, so <c>configuration["evidgate:ci:pepper"]</c> reads the same
/// secret as <c>EVIDGATE:Ci:Pepper</c>.
/// </remarks>
public static class SecretLiteralRules
{
    /// <summary>
    /// The secret keys in both spellings the code could use: the
    /// environment-variable name and its <c>IConfiguration</c> form.
    /// </summary>
    public static IReadOnlyList<string> ForbiddenLiterals { get; } =
    [
        "EVIDGATE__DATABASE__CONNECTIONSTRING",
        "EVIDGATE__TASKS__SWEEPCONNECTIONSTRING",
        "EVIDGATE__CI__SIGNINGKEY",
        "EVIDGATE__CI__PEPPER",
        "EVIDGATE__CI__PREVIOUSPEPPER",
        "EVIDGATE_MIGRATOR__CONNECTIONSTRING",
        "EVIDGATE__OBJECT__ACCESSKEY",
        "EVIDGATE__OBJECT__SECRETKEY",
        "EVIDGATE:Database:ConnectionString",
        "EVIDGATE:Tasks:SweepConnectionString",
        "EVIDGATE:Ci:SigningKey",
        "EVIDGATE:Ci:Pepper",
        "EVIDGATE:Ci:PreviousPepper",
        "EVIDGATE_MIGRATOR:CONNECTIONSTRING",
        "EVIDGATE:Object:AccessKey",
        "EVIDGATE:Object:SecretKey",
    ];

    /// <summary>The <c>IConfiguration</c> extension that reads a connection string by name.</summary>
    public const string ForbiddenCall = "GetConnectionString(";

    /// <summary>
    /// The only files that may read the process environment directly, each with the
    /// reason. Every other <c>Environment.GetEnvironmentVariable(s)</c> call in
    /// <c>src/</c> is a violation: a host reads configuration through
    /// <c>IConfiguration</c> and a secret through <c>ISecretProvider</c>. An exempt
    /// file is still checked for a secret read by its <c>SecretNames</c> constant.
    /// </summary>
    public static IReadOnlyDictionary<string, string> EnvironmentReadExemptions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Evidgate.Migrator/DevSeed.cs"] =
                "reads ASPNETCORE_ENVIRONMENT to refuse seeding outside Development; not a secret",
            ["src/Evidgate.Migrator/MigratorOptions.cs"] =
                "takes the process-environment snapshot the Migrator's ISecretProvider resolves from (the Migrator has no IConfiguration host)",
        };

    /// <summary>
    /// The one file allowed to read configuration or the environment by
    /// <c>MigratorOptions.ConnectionStringVariable</c>. That constant is an alias of
    /// <c>SecretNames.MigratorConnectionString</c> — a name, never a value — so it may
    /// appear in messages and help text anywhere, but only <c>MigratorOptions</c> may
    /// use it as a lookup key.
    /// </summary>
    public const string MigratorAliasReadExemptPath = "src/Evidgate.Migrator/MigratorOptions.cs";

    /// <summary>Any direct read of the process environment.</summary>
    private static readonly Regex EnvironmentReadPattern = new(
        @"\bGetEnvironmentVariables?\s*\(",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// A direct environment read of a secret by its <c>SecretNames</c> constant — the
    /// same bypass as the literal form, written against the constant instead. Checked
    /// in every file, including the environment-read exemptions.
    /// </summary>
    private static readonly Regex SecretConstantEnvironmentReadPattern = new(
        @"\bGetEnvironmentVariable\s*\(\s*(?:[\w.]+\.)?SecretNames\.",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// A configuration or environment lookup keyed by the Migrator's alias of its
    /// secret name: an indexer, a dictionary <c>TryGetValue</c>, an environment read
    /// or a configuration accessor. <c>ISecretProvider</c> calls are not matched.
    /// </summary>
    private static readonly Regex MigratorAliasReadPattern = new(
        @"(?:\[|\b(?:TryGetValue|GetValueOrDefault|GetEnvironmentVariable|GetSection|GetRequiredSection|GetValue(?:<[^>]*>)?)\s*\()\s*(?:[\w.]+\.)?ConnectionStringVariable\b",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// A secret property name used as a configuration key — an indexer
    /// (<c>configuration["ConnectionString"]</c>) or a method argument
    /// (<c>GetSection("ConnectionString")</c>). The negative lookbehind keeps an XML
    /// doc <c>cref="SigningKey"</c> from being mistaken for a read.
    /// </summary>
    private static readonly Regex ForbiddenPropertyPattern = new(
        "(?<![=\\w])\"(ConnectionString|SweepConnectionString|SigningKey|Pepper|PreviousPepper|AccessKey|SecretKey)\"",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// A line holding only the quoted name, optionally followed by a comma: an element
    /// of a multi-line collection (e.g. the redactor's key-marker list), never a
    /// configuration read, which always has an indexer or call on the same line.
    /// </summary>
    private static readonly Regex CollectionElementLine = new(
        "^[ \\t]*\"\\w+\",?[ \\t]*\\r?$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns one line per violation, naming the file and the rule. A file whose
    /// path is in <paramref name="exemptPaths"/> is skipped.
    /// </summary>
    public static IReadOnlyList<string> FindViolations(
        IEnumerable<(string Path, string Text)> files,
        IReadOnlyCollection<string> exemptPaths)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(exemptPaths);

        var violations = new List<string>();

        foreach (var (path, text) in files)
        {
            if (exemptPaths.Contains(path, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var literal in ForbiddenLiterals)
            {
                if (MatchesKey(text, literal))
                {
                    violations.Add($"{path}: secret key literal '{literal}'");
                }
            }

            if (ReadsSecretProperty(text))
            {
                violations.Add($"{path}: a secret property name is read as a configuration key");
            }

            if (text.Contains(ForbiddenCall, StringComparison.Ordinal))
            {
                violations.Add($"{path}: {ForbiddenCall} reads a connection string outside ISecretProvider");
            }

            if (SecretConstantEnvironmentReadPattern.IsMatch(text))
            {
                violations.Add($"{path}: Environment.GetEnvironmentVariable reads a secret outside ISecretProvider");
            }
            else if (!EnvironmentReadExemptions.ContainsKey(path) && EnvironmentReadPattern.IsMatch(text))
            {
                violations.Add($"{path}: reads the process environment directly; use IConfiguration or ISecretProvider");
            }

            if (!string.Equals(path, MigratorAliasReadExemptPath, StringComparison.Ordinal)
                && MigratorAliasReadPattern.IsMatch(text))
            {
                violations.Add($"{path}: reads configuration or the environment by MigratorOptions.ConnectionStringVariable outside MigratorOptions");
            }
        }

        return violations;
    }

    private static bool ReadsSecretProperty(string text)
    {
        foreach (Match match in ForbiddenPropertyPattern.Matches(text))
        {
            var lineStart = text.LastIndexOf('\n', match.Index) + 1;
            var lineEnd = text.IndexOf('\n', match.Index);
            var line = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)];

            if (!CollectionElementLine.IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Matches the key, in any case, only when it is not a prefix of a longer
    /// identifier, so <c>EVIDGATE:Ci:PepperVersion</c> is not mistaken for
    /// <c>EVIDGATE:Ci:Pepper</c>.
    /// </summary>
    private static bool MatchesKey(string text, string literal) =>
        Regex.IsMatch(
            text,
            Regex.Escape(literal) + "(?![A-Za-z0-9_])",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
}
