using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>
/// The <c>Authoring:Generation:Routing:Caps</c> configuration section. Every member defaults to the decided value of
/// <see cref="IntakeCaps.Default"/>, so a host that states nothing runs the decided caps; a member that is stated but
/// out of range stops the host at start (<see cref="IntakeCapsOptionsValidator"/>) instead of loosening a limit.
/// </summary>
internal sealed class IntakeCapsOptions
{
    /// <summary>The configuration path of the section.</summary>
    internal const string SectionName = "Authoring:Generation:Routing:Caps";

    /// <summary>Bytes of one document artifact.</summary>
    public long MaxDocumentBytes { get; set; } = IntakeCaps.Default.MaxDocumentBytes;

    /// <summary>Entries of one DOCX container.</summary>
    public int MaxZipEntries { get; set; } = IntakeCaps.Default.MaxZipEntries;

    /// <summary>Largest uncompressed to compressed ratio of a container.</summary>
    public int MaxCompressionRatio { get; set; } = IntakeCaps.Default.MaxCompressionRatio;

    /// <summary>Pages of one PDF.</summary>
    public int MaxPdfPages { get; set; } = IntakeCaps.Default.MaxPdfPages;

    /// <summary>Characters one extraction may produce.</summary>
    public int MaxExtractedChars { get; set; } = IntakeCaps.Default.MaxExtractedChars;

    /// <summary>Code points of a text intake.</summary>
    public int MaxTextCodePoints { get; set; } = IntakeCaps.Default.MaxTextCodePoints;

    /// <summary>Wall clock of one extraction.</summary>
    public TimeSpan ExtractionTimeout { get; set; } = IntakeCaps.Default.ExtractionTimeout;

    /// <summary>Bytes of one image artifact.</summary>
    public long MaxImageBytes { get; set; } = IntakeCaps.Default.MaxImageBytes;

    /// <summary>Extractions one tenant may run at once.</summary>
    public int MaxConcurrentExtractionsPerTenant { get; set; } = IntakeCaps.Default.MaxConcurrentExtractionsPerTenant;

    /// <summary>The section as one caps value.</summary>
    internal IntakeCaps ToCaps() => new(
        MaxDocumentBytes,
        MaxZipEntries,
        MaxCompressionRatio,
        MaxPdfPages,
        MaxExtractedChars,
        MaxTextCodePoints,
        ExtractionTimeout,
        MaxImageBytes,
        MaxConcurrentExtractionsPerTenant);
}

/// <summary>
/// The one validity rule of <see cref="IntakeCaps"/>: the host section and a stored caps document are judged by the
/// same ranges, so a limit that cannot be configured can never be run from a row either.
/// </summary>
internal static class IntakeCapsRules
{
    private const long ByteCeiling = 1024L * 1024 * 1024;

    /// <summary>The largest extraction the row accepts: <c>authoring_intake_extracted_text_check</c> caps the stored text.</summary>
    internal const int ExtractedCharsCeiling = 2_000_000;

    /// <summary>The largest text intake the row accepts: <c>authoring_intake_input_text_check</c> caps the stored text.</summary>
    internal const int TextCodePointsCeiling = 1_048_576;

    private static readonly TimeSpan TimeoutCeiling = TimeSpan.FromMinutes(5);

    /// <summary>Whether every member of <paramref name="caps"/> is inside its range.</summary>
    /// <param name="caps">The caps.</param>
    internal static bool IsValid(IntakeCaps caps) => !Violations(caps).Any();

    /// <summary>One message per member of <paramref name="caps"/> that is outside its range; empty when the caps are valid.</summary>
    /// <param name="caps">The caps.</param>
    internal static IEnumerable<string> Violations(IntakeCaps caps)
    {
        ArgumentNullException.ThrowIfNull(caps);

        return Ranges(caps);
    }

    private static IEnumerable<string> Ranges(IntakeCaps caps)
    {
        foreach (var failure in new[]
        {
            Check(nameof(caps.MaxDocumentBytes), caps.MaxDocumentBytes, ByteCeiling),
            Check(nameof(caps.MaxImageBytes), caps.MaxImageBytes, ByteCeiling),
            Check(nameof(caps.MaxZipEntries), caps.MaxZipEntries, 100_000),
            Check(nameof(caps.MaxCompressionRatio), caps.MaxCompressionRatio, 10_000),
            Check(nameof(caps.MaxPdfPages), caps.MaxPdfPages, 100_000),
            Check(nameof(caps.MaxExtractedChars), caps.MaxExtractedChars, ExtractedCharsCeiling),
            Check(nameof(caps.MaxTextCodePoints), caps.MaxTextCodePoints, Math.Min(caps.MaxExtractedChars, TextCodePointsCeiling)),
            Check(nameof(caps.MaxConcurrentExtractionsPerTenant), caps.MaxConcurrentExtractionsPerTenant, 64),
            caps.ExtractionTimeout <= TimeSpan.Zero || caps.ExtractionTimeout > TimeoutCeiling
                ? $"{nameof(caps.ExtractionTimeout)} must be above zero and at most 5 minutes."
                : null,
        })
        {
            if (failure is not null)
            {
                yield return failure;
            }
        }
    }

    private static string? Check(string member, long value, long ceiling) =>
        value < 1 || value > ceiling ? $"{member} must be between 1 and {ceiling}." : null;
}

/// <summary>Refuses a caps section that states a non-positive or absurd limit.</summary>
internal sealed class IntakeCapsOptionsValidator : IValidateOptions<IntakeCapsOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, IntakeCapsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = IntakeCapsRules.Violations(options.ToCaps())
            .Select(failure => $"{IntakeCapsOptions.SectionName}:{failure}")
            .ToList();

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>The caps one intake is created under; they are copied onto the row so a later configuration change never re-judges it.</summary>
/// <param name="options">The validated section.</param>
internal sealed class IntakeCapsResolver(IOptions<IntakeCapsOptions> options)
{
    private readonly Lazy<IntakeCaps> _caps = new(() => options.Value.ToCaps());

    /// <summary>The resolved caps.</summary>
    internal IntakeCaps Current => _caps.Value;
}

/// <summary>The one JSON form of <see cref="IntakeCaps"/>: the <c>caps</c> column and the <c>caps</c> member of the responses.</summary>
internal static class IntakeCapsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>The snake_case document of <paramref name="caps"/> (the timeout as <c>hh:mm:ss</c>).</summary>
    /// <param name="caps">The caps.</param>
    internal static string Serialize(IntakeCaps caps) => JsonSerializer.Serialize(caps, Options);

    /// <summary>
    /// Reads a stored document back; <see langword="null"/> when it is not a complete, in-range caps document. A member
    /// this build does not know is ignored, so a newer writer's extra limit never hides the limits this build enforces.
    /// </summary>
    /// <param name="json">The column value.</param>
    internal static IntakeCaps? Deserialize(string json)
    {
        try
        {
            var caps = JsonSerializer.Deserialize<IntakeCaps>(json, Options);

            return caps is not null && IntakeCapsRules.IsValid(caps) ? caps : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
