using System.Text.Json;
using Evidgate.Modules.Usage.Domain;

namespace Evidgate.Modules.Usage.Infrastructure.Parsing;

/// <summary>
/// The member half of <see cref="UsagePriceCardParser"/>: what a name may be echoed as, the refusals collected so far,
/// and the duplicate check the document parser cannot do itself. A name that cannot be decoded or stored is answered
/// with a fixed placeholder, so a caller-controlled name of any length never reaches a refusal.
/// </summary>
internal static partial class UsagePriceCardParser
{
    /// <summary>
    /// The refusals of one card, capped: a body can hold thousands of offending members and a response that returned
    /// all of them would be unbounded. Once the cap is reached the walk stops and one named refusal says so.
    /// </summary>
    private sealed class ViolationSink
    {
        private readonly List<UsagePriceViolation> violations = [];

        /// <summary>How many refusals were collected, including the overflow marker once it was added.</summary>
        internal int Count => violations.Count;

        /// <summary>Whether the cap was reached, so the walk must stop reading further members.</summary>
        internal bool Overflowed { get; private set; }

        internal void Add(string path, string reason)
        {
            if (Overflowed)
            {
                return;
            }

            if (violations.Count >= UsagePriceCardRules.MaxViolations)
            {
                Overflowed = true;
                violations.Add(new UsagePriceViolation("/", UsagePriceRefusalReasons.TooManyViolations));
                return;
            }

            violations.Add(new UsagePriceViolation(path, reason));
        }

        internal void AddRange(IEnumerable<UsagePriceViolation> more)
        {
            foreach (var violation in more)
            {
                Add(violation.Path, violation.Reason);
            }
        }

        internal List<UsagePriceViolation> ToList() => violations;
    }

    /// <summary>
    /// Checks a member name before the walk uses it: a name whose escapes name an unpaired surrogate cannot be
    /// decoded at all, and one that is not text the database can store earns a named refusal either way. The name is
    /// never repeated in that refusal, so a caller-controlled name of any length cannot reach the response.
    /// </summary>
    /// <param name="member">The object member.</param>
    /// <param name="path">The object's JSON pointer.</param>
    /// <param name="violations">The refusals of this card.</param>
    /// <param name="name">The decoded name when the member can be walked.</param>
    /// <returns>Whether the name can be walked as a member.</returns>
    private static bool TryReadMemberName(
        JsonProperty member,
        string path,
        ViolationSink violations,
        out string name)
    {
        try
        {
            name = member.Name;
        }
        catch (InvalidOperationException)
        {
            // System.Text.Json throws when an escape names an unpaired surrogate; not a value the column stores.
            name = string.Empty;
            violations.Add(path + "/" + UnknownSegment, UsagePriceRefusalReasons.InvalidValue);
            return false;
        }

        if (PostgresText.IsStorable(name))
        {
            return true;
        }

        violations.Add(path + "/" + UnknownSegment, UsagePriceRefusalReasons.InvalidValue);
        return false;
    }

    /// <summary>
    /// The pointer of a member: the name when it is a short, storable segment, otherwise a fixed placeholder, so a
    /// refusal never carries a caller-controlled name of any length.
    /// </summary>
    private static string MemberPath(string path, string name) =>
        path + "/" + (PostgresText.IsStorable(name) && CodePointLength(name) <= MaxPathSegmentLength
            ? name
            : UnknownSegment);

    private static void FindDuplicate(JsonElement element, string path, ViolationSink violations)
    {
        HashSet<string>? names = null;

        foreach (var member in element.EnumerateObject())
        {
            if (violations.Overflowed)
            {
                return;
            }

            names ??= new HashSet<string>(StringComparer.Ordinal);

            string name;
            try
            {
                name = member.Name;
            }
            catch (InvalidOperationException)
            {
                // The walk below refuses an undecodable name itself; there is nothing to compare here.
                return;
            }

            if (!names.Add(name))
            {
                violations.Add(MemberPath(path, name), UsagePriceRefusalReasons.DuplicateMember);
                return;
            }
        }
    }
}
