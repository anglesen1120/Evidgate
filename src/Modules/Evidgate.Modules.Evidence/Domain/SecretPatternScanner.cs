namespace Evidgate.Modules.Evidence.Domain;

/// <summary>
/// The closed list of <c>masking_event.pattern_class</c>: the scanner's own vocabulary, the migration's CHECK and the
/// rows a tenant reads all carry these three words and nothing else. A new class is a migration, exactly like every
/// other enum-like column (D4), so a code change cannot widen the stored vocabulary on its own.
/// </summary>
internal static class SecretPatternClass
{
    /// <summary>An AWS-shaped access key id (<c>AKIA</c> and sixteen upper-case letters or digits).</summary>
    public const string AwsAccessKey = "aws_access_key";

    /// <summary>A PEM private-key header, whatever its algorithm (RSA, EC, OpenSSH, encrypted).</summary>
    public const string PrivateKeyPem = "private_key_pem";

    /// <summary>An HTTP <c>Authorization: Bearer …</c> credential long enough to be a token rather than a word.</summary>
    public const string GenericBearerToken = "generic_bearer_token";

    /// <summary>The three classes, in the order a scan reports them.</summary>
    public static readonly IReadOnlyList<string> All = [AwsAccessKey, PrivateKeyPem, GenericBearerToken];

    /// <summary>True when <paramref name="value"/> is one of the three classes.</summary>
    /// <param name="value">The candidate class name.</param>
    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>One class a scan matched and how many times it matched it.</summary>
/// <param name="PatternClass">One of <see cref="SecretPatternClass.All"/>.</param>
/// <param name="HitCount">The number of matches in the scanned bytes; always at least one.</param>
internal readonly record struct SecretPatternHit(string PatternClass, int HitCount);

/// <summary>
/// The starter pattern set of the masking scan: an AWS access-key id, a PEM private-key header and a bearer
/// credential. Deliberately a byte scanner rather than regular expressions — every arm is a literal prefix plus a
/// character class, so one forward pass answers all three in O(n) with no backtracking to bound, no match object that
/// could carry the matched text, and no dependence on how a chunk boundary happens to fall.
/// <para>
/// <b>Nothing matched is ever returned or retained.</b> A matcher advances an index and counts; it never materialises a
/// slice, a string or a captured group, which is what keeps a secret value out of the rows, the logs and the exception
/// messages of every caller (00 §5.7 S3).
/// </para>
/// <para>
/// <b>Bounded by construction.</b> The caller passes bytes it already bounds — the object store caps the read at the
/// artifact kind's <c>max_bytes</c>, whose platform maximum is the storage adapter's own 256 MiB — and this method adds
/// no allocation proportional to the input: one pass over the span, constant counters. It never decodes text, so an
/// arbitrary binary object is safe to scan.
/// </para>
/// </summary>
internal static class SecretPatternScanner
{
    /// <summary>The literal an AWS access-key id starts with.</summary>
    private static readonly byte[] AwsKeyPrefix = "AKIA"u8.ToArray();

    /// <summary>The literal a PEM block starts with (one space, then the algorithm words).</summary>
    private static readonly byte[] PemPrefix = "-----BEGIN "u8.ToArray();

    /// <summary>The words that make a PEM block a private key rather than a certificate or a public key.</summary>
    private static readonly byte[] PemPrivateKeySuffix = "PRIVATE KEY-----"u8.ToArray();

    /// <summary>
    /// The words that make an OpenPGP block a private key: its header spells <c>PRIVATE KEY BLOCK</c>, so the PEM
    /// suffix above does not occur in it.
    /// </summary>
    private static readonly byte[] PgpPrivateKeySuffix = "PRIVATE KEY BLOCK-----"u8.ToArray();

    /// <summary>
    /// The scheme a bearer credential follows. Lower case because the comparison folds both sides before comparing,
    /// so the stored spelling and the scanned spelling can never disagree on case alone.
    /// </summary>
    private static readonly byte[] BearerScheme = "bearer"u8.ToArray();

    /// <summary>The longest algorithm name a PEM private-key header carries, plus its trailing dashes.</summary>
    private const int PemAlgorithmWindow = 40;

    /// <summary>
    /// How far the scan walks between cancellation checks. One check per few MiB keeps a large object responsive to
    /// a lease loss without turning the pass into a per-byte call.
    /// </summary>
    private const int CancellationCheckInterval = 4 * 1024 * 1024;

    /// <summary>The most separator bytes a bearer scheme may be followed by before its credential.</summary>
    private const int BearerSeparatorLimit = 8;

    /// <summary>The shortest credential a bearer scheme is reported for; shorter values are words, not tokens.</summary>
    private const int BearerTokenMinimum = 20;

    /// <summary>Counts every class in one forward pass over <paramref name="content"/>.</summary>
    /// <param name="content">The bytes to scan.</param>
    /// <param name="cancellationToken">
    /// Checked every few MiB of the pass, so a cancelled attempt stops instead of finishing a large object; the caller
    /// owns the retry.
    /// </param>
    /// <param name="progress">
    /// An in-pass checkpoint, called with the offset just before each cancellation check — once at the start and then
    /// once per interval. It is the pass's only observation point from outside, which a cancellation test needs: it can
    /// arm a token once the walk has provably begun instead of reading a scheduler delay as progress. Every caller
    /// passes none.
    /// </param>
    /// <param name="awsIdentifierBoundaries">
    /// False only for an archive's raw container, where an entry name or comment sits directly against the format's
    /// own header bytes, so a key at the edge of a name is flanked by identifier bytes that are not part of it.
    /// </param>
    /// <returns>One entry per class that matched, in <see cref="SecretPatternClass.All"/> order; empty when none did.</returns>
    public static IReadOnlyList<SecretPatternHit> Scan(
        ReadOnlySpan<byte> content,
        CancellationToken cancellationToken,
        Action<int>? progress = null,
        bool awsIdentifierBoundaries = true)
    {
        var aws = 0;
        var pem = 0;
        var bearer = 0;
        var index = 0;
        var nextCancellationCheck = CancellationCheckInterval;

        // Checked once before the walk as well: an attempt already cancelled must not scan even a small object.
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke(0);

        while (index < content.Length)
        {
            if (index >= nextCancellationCheck)
            {
                progress?.Invoke(index);
                cancellationToken.ThrowIfCancellationRequested();
                nextCancellationCheck = index <= content.Length - CancellationCheckInterval
                    ? index + CancellationCheckInterval
                    : int.MaxValue;
            }

            // The three arms are tried at each offset and the cursor then moves past the longest arm's match, so a
            // literal that sits inside another arm's match is never counted twice.
            if (IsAwsAccessKeyAt(content, index, awsIdentifierBoundaries))
            {
                aws++;
                index += AwsKeyPrefix.Length + 16;
                continue;
            }

            if (IsPrivateKeyPemAt(content, index))
            {
                pem++;
                index += PemPrefix.Length;
                continue;
            }

            if (BearerTokenLengthAt(content, index) > 0)
            {
                bearer++;
                index += BearerScheme.Length;
                continue;
            }

            index++;
        }

        if (aws == 0 && pem == 0 && bearer == 0)
        {
            return [];
        }

        var hits = new List<SecretPatternHit>(3);

        if (aws > 0)
        {
            hits.Add(new SecretPatternHit(SecretPatternClass.AwsAccessKey, aws));
        }

        if (pem > 0)
        {
            hits.Add(new SecretPatternHit(SecretPatternClass.PrivateKeyPem, pem));
        }

        if (bearer > 0)
        {
            hits.Add(new SecretPatternHit(SecretPatternClass.GenericBearerToken, bearer));
        }

        return hits;
    }

    /// <summary>
    /// True when an AWS access-key id starts at <paramref name="index"/>: the literal, sixteen upper-case letters or
    /// digits, and a boundary on both sides. The boundaries are what make a longer identifier that merely contains
    /// <c>AKIA</c> a non-match, in both directions.
    /// </summary>
    /// <param name="content">The bytes being scanned.</param>
    /// <param name="index">The offset to test.</param>
    /// <param name="boundaries">Whether identifier bytes on either side veto the match.</param>
    private static bool IsAwsAccessKeyAt(ReadOnlySpan<byte> content, int index, bool boundaries)
    {
        const int bodyLength = 16;

        if (index + AwsKeyPrefix.Length + bodyLength > content.Length)
        {
            return false;
        }

        if (!content.Slice(index, AwsKeyPrefix.Length).SequenceEqual(AwsKeyPrefix))
        {
            return false;
        }

        if (boundaries && index > 0 && IsIdentifierByte(content[index - 1]))
        {
            return false;
        }

        for (var offset = 0; offset < bodyLength; offset++)
        {
            if (!IsUpperAlphanumeric(content[index + AwsKeyPrefix.Length + offset]))
            {
                return false;
            }
        }

        var end = index + AwsKeyPrefix.Length + bodyLength;

        return !boundaries || end == content.Length || !IsIdentifierByte(content[end]);
    }

    /// <summary>
    /// True when a private-key header starts at <paramref name="index"/>: <c>-----BEGIN </c> followed, within the
    /// algorithm window, by the PEM <c>PRIVATE KEY-----</c> suffix or the OpenPGP <c>PRIVATE KEY BLOCK-----</c> one.
    /// The window admits every algorithm spelling (<c>RSA</c>, <c>EC</c>, <c>OPENSSH</c>, <c>ENCRYPTED</c>,
    /// <c>PGP</c>) without a pattern per variant.
    /// </summary>
    /// <param name="content">The bytes being scanned.</param>
    /// <param name="index">The offset to test.</param>
    private static bool IsPrivateKeyPemAt(ReadOnlySpan<byte> content, int index)
    {
        if (index + PemPrefix.Length > content.Length)
        {
            return false;
        }

        if (!content.Slice(index, PemPrefix.Length).SequenceEqual(PemPrefix))
        {
            return false;
        }

        var windowStart = index + PemPrefix.Length;
        var windowLength = Math.Min(PemAlgorithmWindow, content.Length - windowStart);
        var window = content.Slice(windowStart, windowLength);

        return window.IndexOf(PemPrivateKeySuffix) >= 0 || window.IndexOf(PgpPrivateKeySuffix) >= 0;
    }

    /// <summary>
    /// The credential length a bearer scheme at <paramref name="index"/> introduces, capped at
    /// <see cref="BearerTokenMinimum"/>, or zero when the bytes there are not <c>Bearer</c> followed by a bounded
    /// separator run and at least that many token characters. The walk stops once the minimum is reached, so the
    /// lookahead costs a constant number of bytes whatever the credential's length and the pass reaches its own
    /// cancellation check instead of walking the rest of a long run first. The length is returned rather than counted
    /// so the boundary rule has one implementation.
    /// </summary>
    /// <param name="content">The bytes being scanned.</param>
    /// <param name="index">The offset to test.</param>
    private static int BearerTokenLengthAt(ReadOnlySpan<byte> content, int index)
    {
        if (index + BearerScheme.Length > content.Length)
        {
            return 0;
        }

        if (!EqualsAsciiIgnoreCase(content.Slice(index, BearerScheme.Length), BearerScheme))
        {
            return 0;
        }

        if (index > 0 && IsIdentifierByte(content[index - 1]))
        {
            // `XBearer` is one identifier, not a scheme. Every non-alphanumeric byte is a delimiter, so the scheme is
            // still found after `=`, `.`, `;`, a quote or a slash, which is where a header or query string puts it.
            return 0;
        }

        var cursor = index + BearerScheme.Length;
        var separators = 0;

        while (cursor < content.Length && IsBearerSeparator(content[cursor]) && separators < BearerSeparatorLimit)
        {
            separators++;
            cursor++;
        }

        if (separators == 0)
        {
            return 0;
        }

        var observed = 0;

        while (observed < BearerTokenMinimum && cursor < content.Length && IsTokenByte(content[cursor]))
        {
            observed++;
            cursor++;
        }

        return observed == BearerTokenMinimum ? BearerTokenMinimum : 0;
    }

    /// <summary>True for the characters an AWS access-key id's body may hold: <c>[0-9A-Z]</c>.</summary>
    /// <param name="value">The byte to test.</param>
    private static bool IsUpperAlphanumeric(byte value) => value is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z';

    /// <summary>
    /// True for the bytes that continue an identifier: it is the AWS key's boundary on both sides and the bearer
    /// scheme's left boundary, so a scheme is only found where a non-alphanumeric byte (or the start) precedes it.
    /// </summary>
    /// <param name="value">The byte to test.</param>
    private static bool IsIdentifierByte(byte value) => IsUpperAlphanumeric(value) || value is >= (byte)'a' and <= (byte)'z';

    /// <summary>True for the bytes a bearer credential is made of: base64 and base64url, plus the JWT separator.</summary>
    /// <param name="value">The byte to test.</param>
    private static bool IsTokenByte(byte value) =>
        value is >= (byte)'0' and <= (byte)'9'
            or >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'a' and <= (byte)'z'
            or (byte)'.' or (byte)'_' or (byte)'-' or (byte)'+' or (byte)'/' or (byte)'=' or (byte)'~';

    /// <summary>
    /// True for the whitespace a scheme may be followed by before its credential. A line break counts because a header
    /// value can be folded across lines and a log or transcript can wrap between the two.
    /// </summary>
    /// <param name="value">The byte to test.</param>
    private static bool IsBearerSeparator(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    /// <summary>
    /// Compares ASCII bytes ignoring case for the letters, which is how the scheme of a bearer credential is matched:
    /// the scheme is case-insensitive, and every other byte must be equal. Both sides are folded, so neither the
    /// stored literal's spelling nor the scanned spelling can decide the answer on case alone.
    /// </summary>
    /// <param name="value">The bytes being scanned.</param>
    /// <param name="expected">The ASCII literal to compare against.</param>
    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> expected)
    {
        if (value.Length != expected.Length)
        {
            return false;
        }

        for (var offset = 0; offset < value.Length; offset++)
        {
            if (ToLowerAscii(value[offset]) != ToLowerAscii(expected[offset]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The lower-case form of one ASCII letter; every other byte is returned unchanged.</summary>
    /// <param name="value">The byte to fold.</param>
    private static byte ToLowerAscii(byte value) => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value | 0x20) : value;
}
