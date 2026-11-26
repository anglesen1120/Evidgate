using System.Text;
using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>
/// Turns one untrusted requirement document (DOCX or PDF) into plain text, or into one machine failure code.
/// Synchronous, no network, no temp files, no OCR, no AI call. The order is fixed and every step refuses before the
/// next one does work: the declared type must be the one this kind accepts, the stream is read only up to the
/// byte cap, the leading bytes must show the declared container, and only then does a parser see the bytes.
/// The failure code never carries document content.
/// </summary>
/// <param name="parsing">The parser profile; the only zip and XML parser DOCX extraction may use.</param>
/// <param name="timeProvider">The clock of the extraction budget; the system clock when null.</param>
internal sealed class DocumentTextExtractor(IParsing parsing, TimeProvider? timeProvider = null)
{
    /// <summary>The media type a DOCX must be declared as.</summary>
    public const string DocxMediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>The media type a PDF must be declared as.</summary>
    public const string PdfMediaType = "application/pdf";

    private const string DocxKind = "docx";
    private const string PdfKind = "pdf";
    private const int CopyChunkBytes = 81920;

    // The profile's sniff window (the sniffer never looks further), repeated because the profile's own constant is internal.
    private const int SniffPrefixBytes = 4096;

    /// <summary>Extracts the text of one document.</summary>
    /// <param name="content">The document bytes; read from the current position, never past the byte cap.</param>
    /// <param name="kind"><c>docx</c> or <c>pdf</c>; anything else is a container mismatch.</param>
    /// <param name="declaredMediaType">The media type the upload declared; it must equal the kind's type and the sniffed container.</param>
    /// <param name="caps">The caps this intake runs under.</param>
    /// <param name="ct">The caller's cancellation; it propagates as <see cref="OperationCanceledException"/> and is never a refusal.</param>
    /// <returns>The NFC-normalised text, or the one failure code that refused the document.</returns>
    public ExtractionOutcome TryExtract(
        Stream content,
        string kind,
        string declaredMediaType,
        IntakeCaps caps,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(caps);

        using var deadline = new ExtractionDeadline(caps.ExtractionTimeout, ct, timeProvider);

        try
        {
            return ExtractionOutcome.Ok(Extract(content, kind, declaredMediaType, caps, deadline));
        }
        catch (ExtractionRefusedException refused)
        {
            return ExtractionOutcome.Refused(refused.FailureCode);
        }
        catch (OperationCanceledException) when (deadline.BudgetCancelled)
        {
            // A profile call stopped by the linked token: the budget ran out, the caller did not cancel.
            return ExtractionOutcome.Refused(IntakeFailureCode.ExtractionTimeout);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // A parser exception text can quote the document, so it is dropped and only the closed code survives.
            return ExtractionOutcome.Refused(IntakeFailureCode.ExtractionFailed);
        }
    }

    private string Extract(Stream content, string kind, string declaredMediaType, IntakeCaps caps, ExtractionDeadline deadline)
    {
        deadline.ThrowIfExpired();

        var expected = kind switch
        {
            DocxKind => DocxMediaType,
            PdfKind => PdfMediaType,
            _ => throw new ExtractionRefusedException(IntakeFailureCode.ContainerMismatch),
        };

        if (!MediaTypes.Equivalent(declaredMediaType, expected))
        {
            throw new ExtractionRefusedException(IntakeFailureCode.ContainerMismatch);
        }

        var document = ReadBounded(content, caps.MaxDocumentBytes);
        var prefix = document.AsSpan(0, Math.Min(document.Length, SniffPrefixBytes));

        if (parsing.SniffMediaType(declaredMediaType, prefix) != MediaSniffVerdict.Match)
        {
            throw new ExtractionRefusedException(IntakeFailureCode.ContainerMismatch);
        }

        var raw = kind == DocxKind
            ? DocxTextReader.Read(parsing, document, caps, deadline)
            : PdfTextReader.Read(document, caps, deadline);

        // The last page or node batch can finish after the limit: a late result is a timeout, never a success.
        deadline.ThrowIfExpired();

        return Finish(raw, caps.MaxExtractedChars);
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> bytes. A seekable stream is refused from its length before a single
    /// byte is read or allocated; any other stream is refused the moment it delivers one byte more than the cap.
    /// </summary>
    private static byte[] ReadBounded(Stream content, long maxBytes)
    {
        if (content.CanSeek)
        {
            var remaining = content.Length - content.Position;

            if (remaining > maxBytes)
            {
                throw new ExtractionRefusedException(IntakeFailureCode.ArchiveLimitExceeded);
            }

            var exact = new byte[remaining];
            content.ReadExactly(exact);

            return exact;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[CopyChunkBytes];
        int read;

        while ((read = content.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (read > maxBytes - buffer.Length)
            {
                throw new ExtractionRefusedException(IntakeFailureCode.ArchiveLimitExceeded);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Cleans the raw text to what the store accepts (no NUL or other control characters, no lone surrogates), trims
    /// it, normalises it to NFC and re-checks the cap, because normalisation can lengthen a character.
    /// </summary>
    private static string Finish(string raw, int maxChars)
    {
        var cleaned = new StringBuilder(raw.Length);

        for (var index = 0; index < raw.Length; index++)
        {
            var current = raw[index];

            if (char.IsHighSurrogate(current) && index + 1 < raw.Length && char.IsLowSurrogate(raw[index + 1]))
            {
                cleaned.Append(current).Append(raw[++index]);
            }
            else if (char.IsSurrogate(current))
            {
                cleaned.Append('\uFFFD');
            }
            else if (!char.IsControl(current) || current is '\n' or '\t')
            {
                cleaned.Append(current);
            }
        }

        var text = cleaned.ToString().Trim().Normalize(NormalizationForm.FormC);

        if (text.Length == 0)
        {
            throw new ExtractionRefusedException(IntakeFailureCode.NoExtractableText);
        }

        return text.Length > maxChars
            ? throw new ExtractionRefusedException(IntakeFailureCode.CharLimitExceeded)
            : text;
    }
}
