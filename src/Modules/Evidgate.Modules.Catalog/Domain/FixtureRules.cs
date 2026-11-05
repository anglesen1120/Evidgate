using Evidgate.BuildingBlocks.SharedKernel.Security;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>
/// The closed shape of a fixture request: the key grammar, the seed version, the timeout, the cleanup policy and the
/// body pointer. Each rule mirrors the column's CHECK, so a request the API accepts is a row the database accepts.
/// </summary>
internal static class FixtureRules
{
    /// <summary>Upper bound of <c>seed_version</c>, matching the column's CHECK.</summary>
    public const int MaxSeedVersionLength = 64;

    /// <summary>Upper bound of <c>body_ref</c>, matching the column's CHECK and <see cref="RepoPath.MaxLength"/>.</summary>
    public const int MaxBodyRefLength = 1024;

    /// <summary>The cleanup policies the executor understands.</summary>
    public static readonly string[] CleanupPolicies = ["drop", "truncate", "none"];

    /// <summary>The default cleanup policy: everything the fixture created is dropped.</summary>
    public const string DefaultCleanupPolicy = "drop";

    /// <summary>The default timeout, in seconds.</summary>
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>The largest accepted timeout, in seconds (a day).</summary>
    public const int MaxTimeoutSeconds = 86_400;

    /// <summary>
    /// True for a fixture key: 1–64 characters, lower-case letters and digits, a dash allowed after the first
    /// character. Letters are ASCII lower case only, so two keys that differ in case are two fixtures — the same
    /// rule the database enforces, and the reason a key is not a display name.
    /// </summary>
    public static bool IsValidKey(string? key) =>
        key is not null && AsciiShape.IsMatch(key, 1, 64, AsciiShape.LowerAlnum, AsciiShape.LowerAlnumDash);

    /// <summary>True for a seed version: 1–64 characters that are not only whitespace, well-formed and free of control or format characters.</summary>
    public static bool IsValidSeedVersion(string? value) =>
        value is not null && value.Length <= MaxSeedVersionLength && value.Trim().Length > 0 && TextRules.IsClean(value);

    /// <summary>True for a cleanup policy in the closed list (case-sensitive: the stored text is the wire value).</summary>
    public static bool IsValidCleanupPolicy(string? value) =>
        value is not null && Array.IndexOf(CleanupPolicies, value) >= 0;

    /// <summary>
    /// True for a body pointer: a repository-relative path (<see cref="RepoPath"/>) or an artifact id, never longer than
    /// the column and free of any whitespace — the one difference between a pointer and an inline body, and the reason a
    /// value carrying some is refused here and by the CHECK rather than stored and quietly read as a path.
    /// </summary>
    public static bool IsValidBodyRef(string? value) =>
        value is not null
        && value.Length <= MaxBodyRefLength
        && !HasWhitespace(value)
        && (RepoPath.IsValid(value) || Guid.TryParseExact(value, "D", out _));

    /// <summary>
    /// True for any whitespace or separator, not only ASCII: the column's <c>\s</c> is Unicode-aware, so a no-break space
    /// must be refused here instead of failing the insert as a 500.
    /// </summary>
    private static bool HasWhitespace(string value)
    {
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }
}
