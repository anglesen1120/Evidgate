using System.Text.RegularExpressions;

namespace Evidgate.BuildingBlocks.Observability.Redaction;

public sealed partial class SecretRedactor
{
    /// <summary>Connection-string/assignment keywords whose value is a secret.</summary>
    private const string SecretWords =
        "password|passwd|pwd|accountkey|sharedaccesskey|secret|token|apikey|api_key|key|pepper|cookie|credential";

    /// <summary>
    /// A key token that <em>contains</em> one of <see cref="SecretWords"/> — <c>Password</c>,
    /// <c>api-key</c>, <c>SIGNING_KEY</c>, and an <c>EVIDGATE_*</c> name whose secret word is not
    /// the last segment (<c>EVIDGATE__X__SECRET_VALUE</c>, QA D3). The lookbehind starts the token at
    /// a boundary, so a long identifier with no secret word is scanned once, not once per character.
    /// The whole token is the <c>key</c> group, so the replacement keeps it as it was written.
    /// </summary>
    private const string SecretIdentifier =
        $@"(?<key>(?<![A-Za-z0-9_.\-])[A-Za-z0-9_.\-]*?(?:{SecretWords})[A-Za-z0-9_.\-]*)";

    /// <summary>What a masked assignment becomes: the key keeps its own text, so the line stays readable.</summary>
    private const string AssignmentReplacement = "${key}=" + Mask;

    /// <summary>What a masked header becomes (its separator stays a header separator).</summary>
    private const string HeaderReplacement = "${key}: " + Mask;

    /// <summary>
    /// Every literal some pattern needs before it can match: the fast pre-check of
    /// <see cref="MayContainSecret"/> walks this list, and a clean line then skips every pattern.
    /// </summary>
    /// <remarks>
    /// The pre-check is a necessary condition of every pattern, not a heuristic: a pattern can only
    /// match a boundary-anchored identifier that (a) contains one of these literals and (b) is
    /// followed by an optional quote, whitespace and <c>=</c>/<c>:</c>. A new pattern therefore
    /// needs its own literal here <em>and</em> the assignment shape it accepts in
    /// <see cref="MayContainSecret"/>, or the pre-check silently skips it.
    /// </remarks>
    private static readonly string[] TextMarkers =
    [
        "password",
        "passwd",
        "pwd",
        "accountkey",
        "sharedaccesskey",
        "secret",
        "token",
        "apikey",
        "api_key",
        "key",
        "pepper",
        "cookie",
        "credential",
        "authorization",
        "__host-",
        "__secure-",
    ];

    /// <summary>
    /// Masks a double-quoted secret value first: a password containing the <c>;</c>
    /// separator must be quoted (Npgsql both accepts and re-renders
    /// <c>Password="Sup3r;Secret-P4ss"</c>), so an unquoted-only pattern would mask
    /// up to the first <c>;</c> and emit the tail verbatim. Both quote kinds are
    /// handled, including doubled quotes, and the closing quote may not appear in
    /// the value.
    /// </summary>
    /// <remarks>
    /// The key may itself be quoted and the separator may be <c>:</c> (10.3.1-T1.4 QA D1/D2):
    /// <c>"Password"="…"</c> and <c>{"password":"…"}</c> are the shapes a rendered pair or a JSON
    /// body reaches a log line in, and a pattern that only knows <c>key=</c> leaves both intact.
    /// The value may span line breaks, because a quoted secret rendered into a multi-line exception
    /// message reads <c>Password="first\nsecond"</c> and a line-bounded class would mask only up to
    /// the first break and emit the tail; <see cref="MaxTextLength"/> and <see cref="MatchTimeout"/>
    /// bound what such a match may cost. The closing quote must be followed by a delimiter
    /// (<c>; , } ] )</c>, whitespace or the end of the text), so a run like <c>key="key="key="…</c> —
    /// closing quote, then another key — is not read as one quoted value whose tail is then bare
    /// text: that shape falls through to the unquoted pattern, which masks to the end of the line.
    /// The pair is written back without quotes (<c>key=***</c>), so a second pass finds no quoted value to re-wrap;
    /// a mask followed by more text is not a quoted value either, and the unquoted pattern masks its tail.
    /// </remarks>
    private readonly Regex _quotedSecretAssignment = new(
        $@"{SecretIdentifier}[""']?\s*[=:](?>\s*)(?<quote>"")(?<value>(?:\\.|""""|[^""\\])*)\k<quote>(?=$|[\s;,\)\]}}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>Same for single-quoted values (e.g. <c>Password='a;b'</c>, <c>'password': 'a;b'</c>).</summary>
    private readonly Regex _singleQuotedSecretAssignment = new(
        $@"{SecretIdentifier}[""']?\s*[=:](?>\s*)(?<quote>')(?<value>(?:\\.|''|[^'\\])*)\k<quote>(?=$|[\s;,\)\]}}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>
    /// Masks unquoted <c>secret=value</c> pairs anywhere in a text: connection-string
    /// keywords (<c>Password</c>, <c>Pwd</c>, Azure <c>AccountKey</c>/<c>SharedAccessKey</c>),
    /// environment-style assignments such as <c>EVIDGATE__X__TOKEN=…</c>, and their colon
    /// spellings (QA D2/D3). The value runs to the line end, so a value the other patterns
    /// cannot parse is masked rather than half-emitted.
    /// </summary>
    private readonly Regex _secretAssignment = new(
        $@"{SecretIdentifier}[""']?\s*[=:](?>\s*)(?!" + MaskedAssignment + @")(?<value>[^;\r\n]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>
    /// Masks the value of a cookie by its name: a <c>__Host-</c>/<c>__Secure-</c> name is a
    /// session credential whatever the surface it leaks on, and its name carries no secret
    /// word the other patterns know (10.3.1-T1.4, AC2). The name stays, the value becomes
    /// <see cref="Mask"/>.
    /// </summary>
    private readonly Regex _cookieNameAssignment = new(
        @"(?<key>__Host-[A-Za-z0-9_.\-]+|__Secure-[A-Za-z0-9_.\-]+)\s*=(?>\s*)(?!\*\*\*(?=[ \t]*(?:$|[\r\n;,])))(?<value>[^;,\r\n]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>Masks a whole <c>Cookie</c>/<c>Set-Cookie</c> header value, whatever cookie names it carries.</summary>
    private readonly Regex _cookieHeader = new(
        @"(?<key>set-cookie|cookie)\s*:(?>\s*)(?!" + MaskedHeaderLine + @")(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>
    /// A header value that is already <see cref="Mask"/> and nothing else on its line. Anything after the mask (a second
    /// cookie, a scheme tail) is private text and is masked again.
    /// </summary>
    private const string MaskedHeaderLine = @"\*\*\*(?=[ \t]*(?:$|[\r\n]))";

    /// <summary>
    /// An assignment value that is already <see cref="Mask"/>: the mask may end at the line, at a pair separator, or be
    /// followed by the next quoted key (a one-line rendered pair list). Any other text after it is masked again.
    /// </summary>
    private const string MaskedAssignment =
        @"\*\*\*(?=[ \t]*(?:$|[\r\n;]|,[ \t]*[""'][^""'\r\n]{0,200}[""'][ \t]*[=:]))";

    /// <summary>
    /// A quoted-name authorization value that is already <see cref="Mask"/> and nothing else: the mask may be followed
    /// by blanks and a line end, by closing brackets (a one-line JSON or object literal), or by a comma and the next
    /// quoted key. Any other text after the mask is a private tail and is masked again.
    /// </summary>
    private const string MaskedAuthorization =
        @"\*\*\*(?=[ \t]*(?:$|[\r\n]|[}\])](?:[ \t]*(?:$|[\r\n,;}\])]))|,[ \t]*(?:$|[\r\n]|[""'][^""'\r\n]{0,200}[""'][ \t]*:)))";

    /// <summary>
    /// Masks <c>Authorization: Bearer …</c> style header text. A quoted name (<c>"Authorization": "Basic …"</c>, the
    /// JSON and object-literal form) masks the quoted value only, so the rest of a one-line body stays readable.
    /// </summary>
    private readonly Regex _authorizationHeader = new(
        @"(?<key>[""']authorization[""'])\s*:(?>\s*)(?!" + MaskedAuthorization + @")(?<value>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[^\r\n]+)|(?<key>authorization)\s*:(?>\s*)(?!" + MaskedHeaderLine + @")(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>
    /// Masks a GitHub credential by its shape: installation (<c>ghs_</c>), user (<c>ghu_</c>), refresh
    /// (<c>ghr_</c>), OAuth (<c>gho_</c>) and personal-access tokens, and a three-segment JWT. These appear in a
    /// log line with no key naming them — an interpolated token, a header dump — so the assignment patterns
    /// cannot see them, and a bare value has to be recognised by its own shape.
    /// </summary>
    private readonly Regex _githubTokenShapes = new(
        @"\b(?:gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{20,}|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        MatchTimeout);
}
