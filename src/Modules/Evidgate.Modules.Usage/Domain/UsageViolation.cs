namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// One entry rule a batch broke (12.3.1-T1.4): the 00 §9.2 detail is built from it —
/// <see cref="Path"/> is <c>/entries/&lt;index&gt;/&lt;field&gt;</c> for an entry and <c>/entries/&lt;field&gt;</c> for the
/// batch itself, and <see cref="Index"/> is the 0-based position the envelope carries for a batch item.
/// </summary>
/// <param name="Index">The 0-based entry position, or null when the violation belongs to the batch as a whole.</param>
/// <param name="Field">The offending member of the entry, or the batch member; empty when the entry itself is the problem.</param>
/// <param name="Reason">The machine reason: <c>required</c>, <c>invalid_value</c>, <c>too_long</c> or <c>too_many</c>.</param>
internal sealed record UsageViolation(int? Index, string Field, string Reason)
{
    /// <summary>The 00 §9.2 <c>path</c> of this violation.</summary>
    internal string Path => (Index, Field) switch
    {
        (null, { Length: 0 }) => "/entries",
        // The batch member itself is `entries`: its detail names the array, not a field of it.
        (null, "entries") => "/entries",
        (null, _) => "/entries/" + Field,
        ({ } index, { Length: 0 }) => $"/entries/{index}",
        ({ } index, _) => $"/entries/{index}/{Field}",
    };
}
