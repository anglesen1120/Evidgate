using System.Text.RegularExpressions;

namespace Evidgate.Modules.Identity.Domain;

/// <summary>
/// The one email normalisation rule of 02.1.2 §5 ("emails normalised
/// <c>lower(trim())</c> before compare") and the shape it may take.
/// <para>
/// Deliberately ASCII-only: the column check constraint of 0011 is
/// <c>invited_email = lower(invited_email)</c>, and PostgreSQL's <c>lower()</c> is
/// locale-aware while .NET's <c>ToLowerInvariant</c> is not — for a non-ASCII
/// address the two could disagree and the insert would be refused by the database.
/// The API therefore accepts only the printable ASCII subset of RFC 5322 that
/// every provider issues, which makes the two definitions agree by construction.
/// (An internationalised domain name is sent punycoded; that is what the providers
/// report too.)
/// </para>
/// </summary>
internal static class InviteEmail
{
    /// <summary>
    /// A conservative <c>local@domain</c> shape: printable ASCII, at least one dot in
    /// the domain, no whitespace, no consecutive dots in the domain part.
    /// </summary>
    internal const string Shape =
        """^[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+$""";

    /// <summary>The 320-character limit of RFC 5321 for the whole address.</summary>
    internal const int MaxLength = 320;

    private static readonly Regex ShapePattern = new(Shape, RegexOptions.Compiled);

    /// <summary>
    /// True when the address is well-formed <em>after</em> normalisation: the caller may
    /// send surrounding whitespace or mixed case, because both are removed before the
    /// stored value is compared (02.1.2 §5) — so the raw string is not what the shape
    /// describes.
    /// </summary>
    internal static bool IsValid(string email) => ShapePattern.IsMatch(email.Trim());

    /// <summary>The stored, comparable form: trimmed and lowercased (idempotent).</summary>
    internal static string Normalize(string email) =>
        email.Trim().ToLowerInvariant();
}
