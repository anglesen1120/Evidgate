namespace Evidgate.Modules.Identity.Domain;

/// <summary>
/// The alias canonicalisation of the invite role ceiling (D-S81-Q2, closing the S78 hand-over
/// precondition D-S78-M1). One mailbox can be addressed several ways — <c>+tag</c> suffixes at
/// every provider, dots in the local part at Gmail — and an invite addressed to one of those
/// forms binds the same person as the plain address. Two addresses that canonicalise to the same
/// string are the same mailbox for the rules that must not hand a caller a permission they do not
/// hold.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is folded.</b> The local part is cut at its first <c>+</c> for every domain (RFC 5233
/// sub-addressing), and for <c>gmail.com</c> and its legacy <c>googlemail.com</c> spelling the
/// dots of the local part are dropped and <c>googlemail.com</c> is mapped onto <c>gmail.com</c>.
/// Nothing else is folded: a dot in another provider's local part stays significant (it is, at
/// every provider that treats local parts as opaque), so <c>a.b@example.com</c> and
/// <c>ab@example.com</c> stay two different mailboxes.
/// </para>
/// <para>
/// <b>Idempotent, and evidence-blind.</b> The input is already <see cref="InviteEmail.Normalize"/>d
/// by every caller; the function normalises again so it is safe on its own, and
/// <c>Canonical(Canonical(x)) == Canonical(x)</c>. No lookup, no provider call, no DNS: the rule
/// is a pure string fold, so two addresses can never be "maybe the same".
/// </para>
/// <para>
/// <b>Not a substitute for identity.</b> A different mailbox of the same person (an unrelated
/// company address) is indistinguishable from another person here; the rule only refuses the
/// address forms that are provably the same mailbox. The residual is recorded by the plan, not
/// hidden by this type.
/// </para>
/// </remarks>
internal static class EmailAlias
{
    /// <summary>The domain whose local part ignores dots, and the canonical domain every Gmail address maps to.</summary>
    private const string GmailDomain = "gmail.com";

    /// <summary>The legacy Gmail spelling, folded onto <see cref="GmailDomain"/>.</summary>
    private const string GooglemailDomain = "googlemail.com";

    /// <summary>
    /// The comparable form of an address: normalised, plus-tag stripped, and — for Gmail only —
    /// dotless and mapped to <c>gmail.com</c>. Two equal results mean one mailbox.
    /// </summary>
    /// <param name="email">The address, normally already normalised; an empty or malformed value is returned normalised.</param>
    internal static string Canonical(string email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return string.Empty;
        }

        var normalised = InviteEmail.Normalize(email);
        var at = normalised.LastIndexOf('@');

        // Not an address this rule can reason about: return what Normalize said, so the caller
        // compares like with like instead of folding a string that never had a local part.
        if (at <= 0 || at == normalised.Length - 1)
        {
            return normalised;
        }

        var local = normalised[..at];
        var domain = normalised[(at + 1)..];

        // RFC 5233: everything from the first '+' on is a sub-address tag, whatever it contains.
        // A local part that *is* the tag ("+tag@x") keeps its text: stripping it would leave an
        // empty local part and an address nobody owns.
        var plus = local.IndexOf('+');
        if (plus > 0)
        {
            local = local[..plus];
        }

        if (string.Equals(domain, GmailDomain, StringComparison.Ordinal)
            || string.Equals(domain, GooglemailDomain, StringComparison.Ordinal))
        {
            local = local.Replace(".", string.Empty, StringComparison.Ordinal);
            domain = GmailDomain;
        }

        return local.Length == 0
            ? normalised
            : string.Concat(local, "@", domain);
    }
}
