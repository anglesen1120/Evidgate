namespace Evidgate.Modules.Identity.Domain;

/// <summary>
/// The per-organization email-domain allowlist of the bind path (D-S81-Q3, AC5):
/// <c>organization.allowed_email_domains</c> restricts which verified addresses a <c>google</c>
/// login may bind an invite with. A NULL or empty list is "no restriction" — the column's
/// documented meaning since 02.1.1 — and a non-empty one admits only the exact domains it names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exact labels, never prefixes.</b> <c>example.com</c> admits <c>a@example.com</c> and refuses
/// <c>a@sub.example.com</c>: an allowlist that admitted every subdomain would admit tenants of a
/// shared mail host (<c>*.gmail.com</c> is not a domain anyone owns). An entry is compared after
/// trimming and lower-casing it, because the column is written by an admin form and a stray space
/// must not silently disable the gate.
/// </para>
/// <para>
/// <b>Google only.</b> The caller decides that (the binder gates <c>provider == "google"</c>
/// only); this type is the pure comparison, so a GitHub identity is never restricted by it.
/// </para>
/// </remarks>
internal static class EmailDomainAllowlist
{
    /// <summary>
    /// True when <paramref name="normalisedEmail"/>'s domain is admitted by
    /// <paramref name="allowed"/>: unrestricted when the list is null or holds no usable entry,
    /// otherwise an exact (case-insensitive) match against one of its domains.
    /// </summary>
    /// <param name="allowed">The org's <c>allowed_email_domains</c>, as stored.</param>
    /// <param name="normalisedEmail">The provider-verified address, already normalised.</param>
    internal static bool Admits(IReadOnlyList<string>? allowed, string normalisedEmail)
    {
        if (allowed is null || allowed.Count == 0)
        {
            return true;
        }

        var at = normalisedEmail.LastIndexOf('@');
        if (at < 0 || at == normalisedEmail.Length - 1)
        {
            // Not an address: an allowlist that exists cannot admit it, and one that does not
            // exist never asked. Falls out of the same loop below for the second case.
            return false;
        }

        var domain = normalisedEmail[(at + 1)..];
        var restricted = false;

        foreach (var entry in allowed)
        {
            var candidate = entry?.Trim();
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            restricted = true;

            if (string.Equals(candidate, domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Null, empty, and a list of blank entries all mean "this org restricts nothing": a blank
        // entry is a form artefact, not a domain, and refusing every login over one would lock the
        // org out instead of narrowing it.
        return !restricted;
    }
}
