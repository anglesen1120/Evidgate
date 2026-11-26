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
}

/// <summary>Refuses a caps section that states a non-positive or absurd limit.</summary>
internal sealed class IntakeCapsOptionsValidator : IValidateOptions<IntakeCapsOptions>
{
    private const long ByteCeiling = 1024L * 1024 * 1024;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, IntakeCapsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        Check(failures, nameof(options.MaxDocumentBytes), options.MaxDocumentBytes, ByteCeiling);
        Check(failures, nameof(options.MaxImageBytes), options.MaxImageBytes, ByteCeiling);
        Check(failures, nameof(options.MaxZipEntries), options.MaxZipEntries, 100_000);
        Check(failures, nameof(options.MaxCompressionRatio), options.MaxCompressionRatio, 10_000);
        Check(failures, nameof(options.MaxPdfPages), options.MaxPdfPages, 100_000);
        Check(failures, nameof(options.MaxExtractedChars), options.MaxExtractedChars, 100_000_000);
        Check(failures, nameof(options.MaxTextCodePoints), options.MaxTextCodePoints, options.MaxExtractedChars);
        Check(failures, nameof(options.MaxConcurrentExtractionsPerTenant), options.MaxConcurrentExtractionsPerTenant, 64);
        if (options.ExtractionTimeout <= TimeSpan.Zero || options.ExtractionTimeout > TimeSpan.FromMinutes(5))
        {
            failures.Add($"{IntakeCapsOptions.SectionName}:{nameof(options.ExtractionTimeout)} must be above zero and at most 5 minutes.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(List<string> failures, string member, long value, long ceiling)
    {
        if (value < 1 || value > ceiling)
        {
            failures.Add($"{IntakeCapsOptions.SectionName}:{member} must be between 1 and {ceiling}.");
        }
    }
}

/// <summary>The caps one intake is created under; they are copied onto the row so a later configuration change never re-judges it.</summary>
/// <param name="options">The validated section.</param>
internal sealed class IntakeCapsResolver(IOptions<IntakeCapsOptions> options)
{
    private readonly Lazy<IntakeCaps> _caps = new(() =>
    {
        var value = options.Value;
        return new IntakeCaps(
            value.MaxDocumentBytes,
            value.MaxZipEntries,
            value.MaxCompressionRatio,
            value.MaxPdfPages,
            value.MaxExtractedChars,
            value.MaxTextCodePoints,
            value.ExtractionTimeout,
            value.MaxImageBytes,
            value.MaxConcurrentExtractionsPerTenant);
    });

    /// <summary>The resolved caps.</summary>
    internal IntakeCaps Current => _caps.Value;
}

/// <summary>The one JSON form of <see cref="IntakeCaps"/>: the <c>caps</c> column and the <c>caps</c> member of the responses.</summary>
internal static class IntakeCapsJson
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The snake_case document of <paramref name="caps"/> (the timeout as <c>hh:mm:ss</c>).</summary>
    /// <param name="caps">The caps.</param>
    internal static string Serialize(IntakeCaps caps) => JsonSerializer.Serialize(caps, Options);

    /// <summary>Reads a stored document back; <see langword="null"/> when it is not a complete caps document.</summary>
    /// <param name="json">The column value.</param>
    internal static IntakeCaps? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<IntakeCaps>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
