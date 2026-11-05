using System.Buffers;
using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Security;

namespace Evidgate.Modules.Orchestrator.Domain;

/// <summary>
/// The release state vocabulary 0072's <c>release_state_check</c> enumerates: <c>draft</c> and <c>active</c>. The state
/// is set by the create command alone; the only transition the migration's guard permits is <c>draft -> active</c>, and
/// <c>active</c> is final for the component set.
/// </summary>
internal static class ReleaseStates
{
    /// <summary>The state a release is inserted in: its components are still open.</summary>
    internal const string Draft = "draft";

    /// <summary>Terminal: the component set is final and runs may bind.</summary>
    internal const string Active = "active";

    /// <summary>The state a create request that names none gets.</summary>
    internal const string Default = Draft;

    /// <summary>True for the two states the database's <c>CHECK</c> accepts.</summary>
    internal static bool IsKnown(string? state) => state is Draft or Active;
}

/// <summary>
/// Release body rules the route and 0072's <c>CHECK</c>s share (05.6.1 §4/§5): the component-key grammar, the release
/// name bound and the component-count bound. Every bound here mirrors a constraint in the migration, so a writer that
/// skips the route cannot store what the route refuses.
/// </summary>
internal static class ReleaseRules
{
    /// <summary>
    /// Most components one release may declare. The plan set names no limit for a release, so this is the bound the
    /// story sets (documented in 05.6.1 §5): 100 components is far above the largest suite a run manifest carries in
    /// practice, and it keeps the create body inside the route's 64 KiB transport cap.
    /// </summary>
    internal const int MaxComponents = 100;

    /// <summary>The longest release name, the bound <c>release_name_check</c> carries.</summary>
    internal const int MaxNameLength = 128;

    /// <summary>The longest component key, the bound <c>release_component_key_check</c> carries.</summary>
    internal const int MaxKeyLength = 64;

    /// <summary><c>[a-z0-9_-]</c>, the class of a component key's later characters (the environment-key grammar).</summary>
    private static readonly SearchValues<char> KeyTail = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789_-");

    /// <summary>
    /// The component-key grammar <c>[a-z0-9][a-z0-9_-]{0,63}</c>: one forward pass and no wall-clock setting, and a
    /// trailing newline is refused (the <c>\z</c> reading).
    /// </summary>
    internal static bool IsComponentKey(string? key) => AsciiShape.IsMatch(key, 1, MaxKeyLength, AsciiShape.LowerAlnum, KeyTail);

    /// <summary>
    /// A release name is trimmed, 1..128 characters, printable and not credential-shaped: a control character would make
    /// two names look identical to an operator, an untrimmed one would miss the unique key, and a credential-shaped name
    /// would put a secret into the audit row and every screen that shows the release.
    /// </summary>
    internal static bool IsName(string? name) =>
        name is { Length: > 0 and <= MaxNameLength }
        && name == name.Trim()
        && !HasInvisibleOrControl(name)
        && !SecretShape.ValueLooksSecret(name);

    /// <summary>
    /// True when the name carries a character no screen renders as itself: a control character (Cc), an invisible or
    /// formatting one (Cf — zero width, BOM, bidi overrides) or a line/paragraph separator (Zl, Zp). 0072's
    /// <c>release_name_check</c> refuses exactly these classes, so a name this rule accepts can never fail the insert.
    /// </summary>
    private static bool HasInvisibleOrControl(string name)
    {
        foreach (var character in name)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                return true;
            }
        }

        return false;
    }
}
