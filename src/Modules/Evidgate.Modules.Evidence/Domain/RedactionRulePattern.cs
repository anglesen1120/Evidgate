using Evidgate.BuildingBlocks.SharedKernel.Security;

namespace Evidgate.Modules.Evidence.Domain;

/// <summary>
/// The shape of a declared redaction-rule name: 1..<see cref="MaxLength"/> characters of
/// <see cref="AsciiShape.NameGlob"/>, in one forward pass with no wall-clock setting. It is the exact predicate the
/// column's CHECK carries, so a value the route accepts is a value the database stores. The pattern is matched by the
/// reporter, never by the server: <c>*</c> and <c>?</c> are the only metacharacters, and everything else is literal.
/// </summary>
internal static class RedactionRulePattern
{
    /// <summary>The longest declared name the column accepts, in characters.</summary>
    public const int MaxLength = 128;

    /// <summary>True when <paramref name="namePattern"/> is a declared name or glob over one.</summary>
    public static bool IsValid(string? namePattern) =>
        namePattern is not null
        && AsciiShape.IsMatch(namePattern, 1, MaxLength, AsciiShape.NameGlob, AsciiShape.NameGlob);
}
