namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>
/// The closed set of machine reasons an intake document is refused. Each value is a stable snake_case wire string
/// and never carries document content, so it can be stored and shown as is.
/// </summary>
internal static class IntakeFailureCode
{
    /// <summary>The declared media type is not the container the leading bytes show (or not one this kind accepts).</summary>
    public const string ContainerMismatch = "container_mismatch";

    /// <summary>The document carries executable content: a macro package, or a PDF action that runs on open.</summary>
    public const string MacroPresent = "macro_present";

    /// <summary>The PDF is encrypted; no password is ever tried.</summary>
    public const string EncryptionUnsupported = "encryption_unsupported";

    /// <summary>The document points outside itself (DTD or entity, external relationship) or carries an attached file.</summary>
    public const string ExternalReferencePresent = "external_reference_present";

    /// <summary>A byte, entry, ratio, expansion or nesting cap of the container was exceeded.</summary>
    public const string ArchiveLimitExceeded = "archive_limit_exceeded";

    /// <summary>The PDF declares more pages than the cap.</summary>
    public const string PageLimitExceeded = "page_limit_exceeded";

    /// <summary>The extracted text is longer than the character cap.</summary>
    public const string CharLimitExceeded = "char_limit_exceeded";

    /// <summary>The document parsed but holds no text (an image-only PDF, an empty DOCX).</summary>
    public const string NoExtractableText = "no_extractable_text";

    /// <summary>The wall-clock budget ran out.</summary>
    public const string ExtractionTimeout = "extraction_timeout";

    /// <summary>Anything else: malformed container, parser failure, missing required part.</summary>
    public const string ExtractionFailed = "extraction_failed";

    /// <summary>Every value above; a code outside this set is never stored.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ContainerMismatch,
        MacroPresent,
        EncryptionUnsupported,
        ExternalReferencePresent,
        ArchiveLimitExceeded,
        PageLimitExceeded,
        CharLimitExceeded,
        NoExtractableText,
        ExtractionTimeout,
        ExtractionFailed,
    };
}
