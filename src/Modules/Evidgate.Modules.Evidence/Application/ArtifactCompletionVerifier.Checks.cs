using System.Net;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;

namespace Evidgate.Modules.Evidence.Application;

internal sealed partial class ArtifactCompletionVerifier
{
    /// <summary>
    /// The T86 step-3 verification of one stored object against the declared facts (AC3): the stored length must
    /// equal the declared <c>bytes</c> and stay within the effective kind policy's cap, and the SHA-256 the store
    /// recorded must equal the declared one. A store that records no checksum is not verified and fails closed.
    /// Null means the object is verified.
    /// </summary>
    /// <param name="head">What the store reported about the object.</param>
    /// <param name="facts">The declared facts of the row.</param>
    /// <param name="maxBytes">The effective kind policy's size cap (06.1.2-T3.1).</param>
    private static string? Mismatch(ObjectHead head, ArtifactFacts facts, long maxBytes)
    {
        if (facts.Bytes != head.ContentLength)
        {
            return ArtifactFailureCode.SizeMismatch;
        }

        if (head.ContentLength > maxBytes)
        {
            return ArtifactFailureCode.TooLarge;
        }

        // The declared checksum is the 00 §7 content address the mint validated; anything else is not verifiable
        // and is refused rather than compared loosely.
        var declared = facts.Checksum.StartsWith(ChecksumPrefix, StringComparison.Ordinal)
            ? facts.Checksum[ChecksumPrefix.Length..]
            : null;

        return declared is not null && string.Equals(head.ChecksumSha256Hex, declared, StringComparison.OrdinalIgnoreCase)
            ? null
            : ArtifactFailureCode.ChecksumMismatch;
    }

    /// <summary>
    /// The 06.3.1 AC5 sniff of the PENDING object, run after the staging size/cap/checksum verification and
    /// before the promotion (plan §7): the first <see cref="ParsingLimits.SniffPrefixBytes"/> bytes of the
    /// staging object are read and the declared media type is checked against them, so a mismatching artifact is
    /// never copied to the final key. The read is a storage call and runs with no transaction open, exactly like
    /// the <c>HEAD</c>s and the copy (00 §5.7 P5). The read is pinned to the ETag the staging <c>HEAD</c> reported
    /// (<paramref name="stagingETag"/>), so the bytes are the verified object (06.3.1-T2.2).
    /// <para>
    /// Returns the failure code when the completion must be rejected, or <c>null</c> when the promotion may
    /// proceed. Three branches decide it:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// A declared type the profile has no signature for (<see cref="MediaSniffer.HasSignature"/> is false) is a
    /// <c>media_type_mismatch</c> with NO read at all: it can never be verified, so the refusal is decided before
    /// the promotion and both prefix GETs are skipped (06.3.1-T2.2, 00 §5.7 P1).
    /// </item>
    /// <item>
    /// A <see cref="SniffResult.Mismatch"/> on the bytes read is a <c>media_type_mismatch</c>.
    /// </item>
    /// <item>
    /// A store that refuses the conditional read because the object no longer carries the pinned ETag
    /// (<c>412 PreconditionFailed</c>) is a TERMINAL <c>object_missing</c>: the object was replaced between the
    /// <c>HEAD</c> and this read, so the bytes a copy would promote are not the bytes the size/checksum checks
    /// verified, and the copy is never attempted. This is deliberately different from a staging read that finds
    /// the object GONE (<c>null</c>, the 24 h lifecycle race): that returns <c>null</c> so the promotion proceeds
    /// and the copy reports the missing source, letting the caller fall through to the retry path — an absent
    /// object is recoverable, a replaced one is not.
    /// </item>
    /// </list>
    /// <para>
    /// When the store reports no ETag at <c>HEAD</c> (<paramref name="stagingETag"/> is <c>null</c>) the read is
    /// unpinned — the store offers nothing to pin it to. That is bounded rather than fail-open: the promoted
    /// object's own SHA-256 is re-<c>HEAD</c>ed after the copy and compared against the declared content address
    /// (<c>checksum_mismatch</c>), so an object swapped in that window is still refused before the artifact
    /// becomes <c>ready</c>.
    /// </para>
    /// </summary>
    /// <param name="stagingKey">The derived <c>pending/</c> staging key.</param>
    /// <param name="facts">The declared facts of the row (the media type is sniffed against the bytes).</param>
    /// <param name="stagingETag">The ETag the staging <c>HEAD</c> reported, or <c>null</c> when the store reports none.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    private async ValueTask<string?> StagingSniffFailureAsync(
        string stagingKey, ArtifactFacts facts, string? stagingETag, CancellationToken cancellationToken)
    {
        // Fail closed without a read (06.3.1-T2.2): a declared type the profile has no signature for can never be
        // verified, so it is a Mismatch by definition. The refusal is decided here — before the promotion — and
        // both prefix GETs are skipped, so an unsigned declared type costs no storage call (00 §5.7 P1).
        if (!MediaSniffer.HasSignature(facts.MediaType))
        {
            return ArtifactFailureCode.MediaTypeMismatch;
        }

        byte[]? prefix;
        try
        {
            prefix = await storage
                .ReadPrefixAsync(stagingKey, ParsingLimits.SniffPrefixBytes, cancellationToken, stagingETag)
                .ConfigureAwait(false);
        }
        catch (ObjectStorageException exception) when (IsPreconditionFailed(exception))
        {
            // The staging object is no longer the one the HEAD verified: the bytes a copy would promote are not
            // the verified bytes, so the completion is refused rather than promoted (06.3.1-T2.2).
            return ArtifactFailureCode.ObjectMissing;
        }

        return prefix is not null && MediaSniffer.Check(facts.MediaType, prefix) == SniffResult.Mismatch
            ? ArtifactFailureCode.MediaTypeMismatch
            : null;
    }

    /// <summary>
    /// The 06.3.1 AC5 sniff of the FINAL object (S-F2): once the final-key size/cap/checksum verification has
    /// passed, the first <see cref="ParsingLimits.SniffPrefixBytes"/> bytes of the FINAL object are read and the
    /// declared media type is checked against them again, because the bytes could have been swapped between the
    /// staging read and the copy. The read is a storage call and runs with no transaction open, exactly like the
    /// <c>HEAD</c>s and the copy above (00 §5.7 P5) and is pinned to the ETag the final <c>HEAD</c> reported
    /// (<paramref name="finalETag"/>), so the bytes are the verified object (06.3.1-T2.2). A mismatch rejects
    /// with <c>media_type_mismatch</c>; a prefix that is gone (the object vanished between the <c>HEAD</c> and
    /// this read) rejects with <c>object_missing</c>; a store that reports the pinned object changed (<c>412</c>)
    /// rejects with <c>object_missing</c> too — never a 500, never <c>ready</c>.
    /// <para>
    /// A declared type with no signature is a <c>media_type_mismatch</c> with no read here as well, the same
    /// fail-closed rule the staging sniff applies (the staging sniff already refuses such an artifact, so this
    /// arm is a belt-and-braces guard, not a reachable path). When the store reports no ETag at the final
    /// <c>HEAD</c> (<paramref name="finalETag"/> is <c>null</c>) the read is unpinned; the SHA-256 comparison the
    /// caller already ran against the final <c>HEAD</c> is what bounds that case.
    /// </para>
    /// </summary>
    /// <param name="facts">The declared facts of the row (the media type is sniffed against the bytes).</param>
    /// <param name="verifiedBytes">The length the final <c>HEAD</c> reported, carried into an accepted verdict.</param>
    /// <param name="finalETag">The ETag the final <c>HEAD</c> reported, or <c>null</c> when the store reports none.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    private async ValueTask<ArtifactCompletionVerdict> SniffAsync(
        ArtifactFacts facts, long verifiedBytes, string? finalETag, CancellationToken cancellationToken)
    {
        if (!MediaSniffer.HasSignature(facts.MediaType))
        {
            return ArtifactCompletionVerdict.Reject(ArtifactFailureCode.MediaTypeMismatch);
        }

        byte[]? prefix;
        try
        {
            prefix = await storage
                .ReadPrefixAsync(facts.ObjectKey, ParsingLimits.SniffPrefixBytes, cancellationToken, finalETag)
                .ConfigureAwait(false);
        }
        catch (ObjectStorageException exception) when (IsPreconditionFailed(exception))
        {
            return ArtifactCompletionVerdict.Reject(ArtifactFailureCode.ObjectMissing);
        }

        if (prefix is null)
        {
            return ArtifactCompletionVerdict.Reject(ArtifactFailureCode.ObjectMissing);
        }

        return MediaSniffer.Check(facts.MediaType, prefix) == SniffResult.Mismatch
            ? ArtifactCompletionVerdict.Reject(ArtifactFailureCode.MediaTypeMismatch)
            : ArtifactCompletionVerdict.Accept(verifiedBytes);
    }

    /// <summary>
    /// Copies the verified staging object to the final key (T86 step 4, idempotent on the destination). False when
    /// the store reports the source missing: the caller then checks the final key, exactly like the retry path,
    /// instead of turning a lifecycle race into a 500.
    /// </summary>
    /// <param name="stagingKey">The derived staging key.</param>
    /// <param name="objectKey">The final key the row holds.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    private async ValueTask<bool> TryPromoteAsync(string stagingKey, string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            await storage.CopyAsync(stagingKey, objectKey, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (ObjectStorageException exception) when (IsAbsent(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// True when the store reported the source object absent: the adapter's own 404 rule (<c>HeadAsync</c> answers
    /// null for the same three answers, so the copy path reads the race the same way).
    /// </summary>
    /// <param name="exception">The failed storage call.</param>
    private static bool IsAbsent(ObjectStorageException exception) =>
        exception.HttpStatus == HttpStatusCode.NotFound
        || string.Equals(exception.S3ErrorCode, "NoSuchKey", StringComparison.Ordinal)
        || string.Equals(exception.S3ErrorCode, "NotFound", StringComparison.Ordinal);

    /// <summary>
    /// True when the store refused a conditional prefix read because the object no longer carries the ETag the
    /// preceding <c>HEAD</c> reported (06.3.1-T2.2): <c>412 PreconditionFailed</c>. The verified object is gone
    /// or replaced, so the caller treats it as absent and fails closed — never promotes or commits unverified bytes.
    /// </summary>
    /// <param name="exception">The failed storage call.</param>
    private static bool IsPreconditionFailed(ObjectStorageException exception) =>
        exception.HttpStatus == HttpStatusCode.PreconditionFailed
        || string.Equals(exception.S3ErrorCode, "PreconditionFailed", StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="mediaType"/> is one of the policy's accepted types (06.1.2 §5, AC3): the bare
    /// type (parameters after <c>;</c> stripped) is compared case-insensitively and <em>verbatim</em> — surrounding
    /// whitespace and control characters are not trimmed away — and a control character anywhere in the type is
    /// refused. The same comparison the mint applies (06.1.2-T1), so both ends of the upload agree on what the
    /// policy accepts.
    /// </summary>
    /// <param name="allowed">The effective policy's accepted types (never null: a null resolution is refused before this call).</param>
    /// <param name="mediaType">The declared media type of the artifact.</param>
    private static bool MediaTypeAllowed(string[] allowed, string? mediaType)
    {
        if (mediaType is null || !ArtifactShape.HasNoControlCharacter(mediaType))
        {
            return false;
        }

        var bare = mediaType.Split(';', 2)[0];

        return allowed.Any(candidate => string.Equals(candidate, bare, StringComparison.OrdinalIgnoreCase));
    }
}
