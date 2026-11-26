namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>
/// The limits one requirement-document intake runs under. The values are resolved from configuration by the caller
/// and persisted with the intake row, so a later extraction uses exactly the numbers the upload was admitted under;
/// <see cref="Default"/> is the decided baseline and the only place the literals live.
/// </summary>
/// <param name="MaxDocumentBytes">The most bytes one document may hold; the extractor never reads past this.</param>
/// <param name="MaxZipEntries">The most entries a DOCX package may hold.</param>
/// <param name="MaxCompressionRatio">The most one zip entry may expand against the bytes it compressed from.</param>
/// <param name="MaxPdfPages">The most pages a PDF may declare; checked before any page is decoded.</param>
/// <param name="MaxExtractedChars">The most characters extraction may produce; one more is refused, never truncated.</param>
/// <param name="MaxTextCodePoints">The most code points the stored requirement text may hold (applied by the caller).</param>
/// <param name="ExtractionTimeout">The wall-clock budget of one extraction.</param>
/// <param name="MaxImageBytes">The most bytes one image attachment may hold (applied by the caller).</param>
/// <param name="MaxConcurrentExtractionsPerTenant">The most extractions one tenant may run at once (applied by the caller).</param>
internal sealed record IntakeCaps(
    long MaxDocumentBytes,
    int MaxZipEntries,
    int MaxCompressionRatio,
    int MaxPdfPages,
    int MaxExtractedChars,
    int MaxTextCodePoints,
    TimeSpan ExtractionTimeout,
    long MaxImageBytes,
    int MaxConcurrentExtractionsPerTenant)
{
    /// <summary>The decided baseline: 25 MiB, 2000 entries, 100:1, 200 pages, 1 000 000 characters, 200 000 code points, 20 s, 10 MiB, 2.</summary>
    public static IntakeCaps Default { get; } = new(
        MaxDocumentBytes: 25L * 1024 * 1024,
        MaxZipEntries: 2000,
        MaxCompressionRatio: 100,
        MaxPdfPages: 200,
        MaxExtractedChars: 1_000_000,
        MaxTextCodePoints: 200_000,
        ExtractionTimeout: TimeSpan.FromSeconds(20),
        MaxImageBytes: 10L * 1024 * 1024,
        MaxConcurrentExtractionsPerTenant: 2);
}
