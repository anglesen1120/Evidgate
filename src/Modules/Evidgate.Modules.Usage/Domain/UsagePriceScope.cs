namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// Which rate card a load writes: the global card every org falls back to, or the caller's own override.
/// The org is never part of the card — a load on <see cref="Org"/> writes for the open tenant scope, so a card cannot
/// name another tenant.
/// </summary>
internal enum UsagePriceScope
{
    /// <summary>The caller's per-org override (an ordinary tenant-scoped insert).</summary>
    Org,

    /// <summary>
    /// The global fallback card. Its rows carry no org, so only the security-definer function can write them; the app
    /// role's own insert policy refuses a null <c>org_id</c>.
    /// </summary>
    Global,
}
