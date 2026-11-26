using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Exceptions;

namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>
/// Reads the text of a PDF with PdfPig, text only: no images, no OCR, no form fields, no annotations. The order
/// is the safety order: forbidden names in the raw bytes, then encryption, then the page cap, then the
/// reachable-object walk, and only then the first page is decoded.
/// </summary>
internal static class PdfTextReader
{
    /// <summary>Extracts the text of every page.</summary>
    /// <param name="document">The PDF bytes, already bounded and sniffed.</param>
    /// <param name="caps">The caps of this intake.</param>
    /// <param name="deadline">The wall-clock budget, checked before each page.</param>
    /// <returns>The raw text; the caller normalises it.</returns>
    /// <exception cref="ExtractionRefusedException">The PDF breaks a cap or a safety rule.</exception>
    public static string Read(byte[] document, IntakeCaps caps, ExtractionDeadline deadline)
    {
        deadline.ThrowIfExpired();
        PdfActionScan.ProbeRaw(document, deadline);

        using var pdf = Open(document);

        if (pdf.IsEncrypted)
        {
            throw new ExtractionRefusedException(IntakeFailureCode.EncryptionUnsupported);
        }

        if (pdf.NumberOfPages > caps.MaxPdfPages)
        {
            throw new ExtractionRefusedException(IntakeFailureCode.PageLimitExceeded);
        }

        PdfActionScan.WalkReachable(pdf, deadline);

        return ReadPages(pdf, caps, deadline);
    }

    private static PdfDocument Open(byte[] document)
    {
        try
        {
            return PdfDocument.Open(document, new ParsingOptions { SkipMissingFonts = true });
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new ExtractionRefusedException(IntakeFailureCode.EncryptionUnsupported);
        }
    }

    private static string ReadPages(PdfDocument pdf, IntakeCaps caps, ExtractionDeadline deadline)
    {
        var text = new TextAccumulator(caps.MaxExtractedChars);

        for (var number = 1; number <= pdf.NumberOfPages; number++)
        {
            deadline.ThrowIfExpired();

            var page = pdf.GetPage(number);

            // A word holds at least one letter, so a page with more than twice the remaining budget in letters
            // cannot fit: refuse before the word extractor spends time on it.
            if (page.Letters.Count > 2L * (caps.MaxExtractedChars - text.Length) + 1)
            {
                throw new ExtractionRefusedException(IntakeFailureCode.CharLimitExceeded);
            }

            text.BreakLine();
            AppendWords(page, text);
        }

        return text.ToString();
    }

    /// <summary>Joins the words of a page, starting a new line when the baseline moves by more than half a word height.</summary>
    private static void AppendWords(Page page, TextAccumulator text)
    {
        Word? previous = null;

        foreach (var word in page.GetWords())
        {
            if (previous is not null)
            {
                var moved = Math.Abs(word.BoundingBox.Bottom - previous.BoundingBox.Bottom) > previous.BoundingBox.Height / 2;

                if (moved)
                {
                    text.BreakLine();
                }
                else
                {
                    text.Append(' ');
                }
            }

            text.Append(word.Text);
            previous = word;
        }
    }
}
