using System.Reflection;
using System.Text;

namespace Evidgate.BuildingBlocks.Persistence.Isolation;

/// <summary>
/// What one <c>contracts/rls-exceptions.txt</c> entry excuses a table from
/// (02.6.1-T1.4, 00 §5.4.1/§5.4.6/§5.4.7, area plan 02 §4.5; TL T3, T4):
/// <list type="bullet">
///   <item><see cref="Platform"/> — no <c>org_id</c> column at all; the table is
///   outside the org scope (02 D12). Skips the org-scoped-policy requirement, keeps
///   the RLS-flag and policy-presence requirement.</item>
///   <item><see cref="Catalogue"/> — nullable <c>org_id</c>; platform catalogue data
///   readable by every org, writable only by the owner role. Skips the
///   <c>org_id NOT NULL</c>/org-scoped requirement, keeps the flags and requires the
///   all-org read policy shape.</item>
///   <item><see cref="Mechanism"/> — 01's mechanism tables: they carry <c>org_id</c>
///   but deliberately hold no policy, because the dispatcher and the claim scan walk
///   every org by design (00 §5.3). Outside the rule set entirely.</item>
/// </list>
/// </summary>
public enum RlsExceptionKind
{
    /// <summary>No <c>org_id</c> column; reached through membership/resolvers, never an org-scoped select.</summary>
    Platform,

    /// <summary>Nullable <c>org_id</c>; built-in rows are platform catalogue, readable by every org.</summary>
    Catalogue,

    /// <summary>A mechanism table with no RLS policy by design (outbox, task, journal).</summary>
    Mechanism,
}

/// <summary>One parsed line of the RLS exception list.</summary>
/// <param name="Table">Schema-qualified table name, exactly as PostgreSQL reports it (<c>public.role</c>).</param>
/// <param name="Kind">What the entry excuses the table from.</param>
/// <param name="Reason">The free-text reason after the <c>#</c>, or an empty string.</param>
public sealed record RlsExceptionEntry(string Table, RlsExceptionKind Kind, string Reason);

/// <summary>
/// The RLS exception list of 02.6.1-T1.4: the single source that says which tables
/// are not org-scoped. <c>contracts/rls-exceptions.txt</c> is embedded into this
/// assembly at build time, so the boot self-check of <c>Evidgate.Api</c> and
/// <c>Evidgate.Worker</c> and the RLS coverage test of 02.6.1-T1 always judge the
/// same list as the CI lint of 02.6.1-T2 — a published app cannot be started, and a
/// container cannot be deployed, without it.
/// <para>
/// Format: one <c>public.&lt;table&gt; &lt;kind&gt;</c> pair per line, an optional
/// <c># reason</c>, and blank lines or whole-line comments (<c>#</c>) ignored. A
/// malformed line fails loudly and names itself: an exception list that silently
/// swallows a typo excuses nothing while looking loaded.
/// </para>
/// </summary>
public sealed class RlsExceptionList
{
    /// <summary>Repository-relative path of the file this list is built from.</summary>
    public const string RelativePath = "contracts/rls-exceptions.txt";

    /// <summary>The embedded-resource name the build gives <see cref="RelativePath"/>.</summary>
    public const string EmbeddedResourceName =
        "Evidgate.BuildingBlocks.Persistence.rls-exceptions.txt";

    /// <summary>Named failure code when the embedded list is missing from the assembly.</summary>
    public const string MissingCode = "rls_exception_list_missing";

    /// <summary>Named failure code for a malformed line (checker and lint read the same file).</summary>
    public const string InvalidCode = "rls_exception_list_invalid";

    private readonly Dictionary<string, RlsExceptionEntry> _byTable;

    private RlsExceptionList(IReadOnlyList<RlsExceptionEntry> entries)
    {
        Entries = entries;
        _byTable = entries.ToDictionary(entry => entry.Table, StringComparer.Ordinal);
    }

    /// <summary>Every entry, in file order.</summary>
    public IReadOnlyList<RlsExceptionEntry> Entries { get; }

    /// <summary>The entry for one schema-qualified table, or <c>null</c> when it is not excused.</summary>
    public RlsExceptionEntry? Find(string qualifiedTable) =>
        _byTable.TryGetValue(qualifiedTable, out var entry) ? entry : null;

    /// <summary>The kind a table is excused as, or <c>null</c> when it is not listed at all.</summary>
    public RlsExceptionKind? KindOf(string qualifiedTable) => Find(qualifiedTable)?.Kind;

    /// <summary>Parses the list text. Throws <see cref="InvalidOperationException"/> on a malformed line.</summary>
    public static RlsExceptionList Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var entries = new List<RlsExceptionEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = text.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var comment = line.IndexOf('#', StringComparison.Ordinal);
            var body = comment >= 0 ? line[..comment] : line;
            var tokens = body.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var number = index + 1;

            if (tokens.Length != 2 || !IsSchemaQualifiedTable(tokens[0]))
            {
                throw new InvalidOperationException(
                    $"{InvalidCode}: {RelativePath} line {number}: expected "
                    + "'<schema>.<table> <platform|catalogue|mechanism> # reason', found "
                    + $"'{line}'");
            }

            var kind = tokens[1] switch
            {
                "platform" => RlsExceptionKind.Platform,
                "catalogue" => RlsExceptionKind.Catalogue,
                "mechanism" => RlsExceptionKind.Mechanism,
                _ => throw new InvalidOperationException(
                    $"{InvalidCode}: {RelativePath} line {number}: unknown kind '{tokens[1]}' — "
                    + "expected platform, catalogue or mechanism"),
            };

            if (!seen.Add(tokens[0]))
            {
                throw new InvalidOperationException(
                    $"{InvalidCode}: {RelativePath} line {number}: '{tokens[0]}' is listed twice — "
                    + "one table has one kind");
            }

            entries.Add(new RlsExceptionEntry(
                tokens[0],
                kind,
                comment >= 0 ? line[(comment + 1)..].Trim() : string.Empty));
        }

        return new RlsExceptionList(entries);
    }

    /// <summary>Loads the list embedded into the assembly that declares <see cref="RlsExceptionList"/>.</summary>
    public static RlsExceptionList LoadEmbedded() => LoadEmbedded(typeof(RlsExceptionList).Assembly);

    /// <summary>Loads the list embedded into <paramref name="assembly"/> (the test seam of the coverage suite).</summary>
    public static RlsExceptionList LoadEmbedded(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"{MissingCode}: '{EmbeddedResourceName}' is not embedded in "
                + $"'{assembly.GetName().Name}' — the build must embed {RelativePath} "
                + "(the RLS exception list is the single source of the isolation self-check).");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// A lowercase PostgreSQL identifier, optionally dot-qualified once:
    /// <c>public.role</c>. The table token is schema-qualified on purpose — a bare
    /// <c>role</c> could silently excuse the wrong table once a module owns a second
    /// schema.
    /// </summary>
    private static bool IsSchemaQualifiedTable(string token)
    {
        var dot = token.IndexOf('.', StringComparison.Ordinal);
        return dot > 0
            && dot == token.LastIndexOf('.')
            && IsIdentifier(token.AsSpan(0, dot))
            && IsIdentifier(token.AsSpan(dot + 1));
    }

    private static bool IsIdentifier(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value[0] is not ((>= 'a' and <= 'z') or '_'))
        {
            return false;
        }

        foreach (var character in value[1..])
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
