namespace Evidgate.Modules.Healing.Contracts;

/// <summary>
/// The value of the healing kill switch (08.6.3 §3, 00 §8.7). A row can only ever hold the first three values
/// (`healing_switch_mode_check`, AC2); <see cref="Unavailable"/> is a runtime observation of a dependency that
/// 08.6.3-T2 produces from its health probes, never a configured mode, and this story's resolver never returns it.
/// </summary>
public enum HealingAvailability
{
    /// <summary>Healing may act on a validated candidate.</summary>
    Enabled = 0,

    /// <summary>Healing decides and records, but never applies anything: the V1 posture (feature plan 08.6.3 §3).</summary>
    Shadow = 1,

    /// <summary>Healing does nothing for the scope.</summary>
    Disabled = 2,

    /// <summary>A dependency healing needs is unhealthy, so healing is off whatever the configuration says (T2).</summary>
    Unavailable = 3,
}

/// <summary>
/// The wire vocabulary of <see cref="HealingAvailability"/> and its strict parsing: the four snake_case values of
/// 00 §8.7, of which only <see cref="Enabled"/>, <see cref="Shadow"/> and <see cref="Disabled"/> are storable
/// (<c>healing_switch_mode_check</c>, migration 0052). One place for the mapping, so the resolver, the routes and the
/// tests cannot drift apart.
/// </summary>
public static class HealingAvailabilityModes
{
    /// <summary>The wire value of <see cref="HealingAvailability.Enabled"/>.</summary>
    public const string Enabled = "enabled";

    /// <summary>The wire value of <see cref="HealingAvailability.Shadow"/>.</summary>
    public const string Shadow = "shadow";

    /// <summary>The wire value of <see cref="HealingAvailability.Disabled"/>.</summary>
    public const string Disabled = "disabled";

    /// <summary>The wire value of <see cref="HealingAvailability.Unavailable"/>: reported, never stored.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>
    /// The three values a switch row may hold, in restrictive order (AC2): <c>enabled</c> &lt; <c>shadow</c> &lt;
    /// <c>disabled</c>. <c>unavailable</c> is deliberately absent — a configured value cannot be an observation.
    /// </summary>
    public static IReadOnlyList<string> Storable { get; } = [Enabled, Shadow, Disabled];

    /// <summary>Whether <paramref name="mode"/> is one of the three storable values.</summary>
    /// <param name="mode">The wire value a route received or a row holds.</param>
    public static bool IsStorable(string? mode) =>
        mode is Enabled or Shadow or Disabled;

    /// <summary>The wire value of <paramref name="availability"/>.</summary>
    /// <param name="availability">The mode.</param>
    public static string ToWire(HealingAvailability availability) => availability switch
    {
        HealingAvailability.Enabled => Enabled,
        HealingAvailability.Shadow => Shadow,
        HealingAvailability.Disabled => Disabled,
        _ => Unavailable,
    };

    /// <summary>
    /// Parses one wire value. Unknown text (including <c>null</c> and the empty string) is refused rather than
    /// defaulted: a row the database accepted always parses, and anything else is a defect worth surfacing.
    /// </summary>
    /// <param name="mode">The wire value.</param>
    /// <param name="availability">The parsed mode when this returns true.</param>
    public static bool TryParse(string? mode, out HealingAvailability availability)
    {
        switch (mode)
        {
            case Enabled:
                availability = HealingAvailability.Enabled;
                return true;
            case Shadow:
                availability = HealingAvailability.Shadow;
                return true;
            case Disabled:
                availability = HealingAvailability.Disabled;
                return true;
            case Unavailable:
                availability = HealingAvailability.Unavailable;
                return true;
            default:
                availability = default;
                return false;
        }
    }
}

/// <summary>
/// The pure resolution of the kill switch (08.6.3 §3, AC2/AC7): the most restrictive mode of the scopes that carry a
/// row wins, and a database with no row at all resolves to <see cref="HealingAvailability.Shadow"/> — the V1 posture
/// of healing that decides but never applies. No health probe is consulted here; <see cref="HealingAvailability.Unavailable"/>
/// joins the order only when 08.6.3-T2 hands one in, where it outranks <see cref="HealingAvailability.Disabled"/>.
/// </summary>
public static class HealingAvailabilityResolution
{
    /// <summary>The resolved mode when no scope carries a row (feature plan 08.6.3 §3: shadow healing in V1).</summary>
    public const HealingAvailability NoRowDefault = HealingAvailability.Shadow;

    /// <summary>
    /// Resolves the persisted modes: the most restrictive one wins (<c>disabled</c> &gt; <c>shadow</c> &gt;
    /// <c>enabled</c>, and <c>unavailable</c> above all), or <see cref="NoRowDefault"/> when
    /// <paramref name="persisted"/> is empty.
    /// </summary>
    /// <param name="persisted">The modes of the scopes that have a row, in any order and any number (distinct scopes).</param>
    public static HealingAvailability Resolve(IEnumerable<HealingAvailability> persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);

        var any = false;
        var mostRestrictive = HealingAvailability.Enabled;
        foreach (var mode in persisted)
        {
            if (!any || Rank(mode) > Rank(mostRestrictive))
            {
                mostRestrictive = mode;
                any = true;
            }
        }

        return any ? mostRestrictive : NoRowDefault;
    }

    /// <summary>
    /// The restrictive order as an integer: a higher rank is stricter. Only this order decides, so the enum's
    /// declaration order is never load-bearing.
    /// </summary>
    /// <param name="availability">The mode.</param>
    public static int Rank(HealingAvailability availability) => availability switch
    {
        HealingAvailability.Enabled => 1,
        HealingAvailability.Shadow => 2,
        HealingAvailability.Disabled => 3,
        _ => 4,
    };
}

/// <summary>
/// The in-module reader other modules ask "may healing act for this project?" (00 §5.5, the Contracts seam). It runs
/// in the caller's tenant scope and reads the three scope rows in one statement (AC9); the answer is the most
/// restrictive mode and never <see cref="HealingAvailability.Unavailable"/> in this story.
/// </summary>
public interface IHealingAvailabilityResolver
{
    /// <summary>
    /// Resolves the healing availability in force for the caller's org, narrowed to <paramref name="projectId"/>
    /// when a project scope is asked for.
    /// </summary>
    /// <param name="projectId">The project whose override applies, or null for the org-level answer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The most restrictive persisted mode, or the V1 default when no scope carries a row.</returns>
    ValueTask<HealingAvailability> ResolveAsync(Guid? projectId, CancellationToken cancellationToken);
}
