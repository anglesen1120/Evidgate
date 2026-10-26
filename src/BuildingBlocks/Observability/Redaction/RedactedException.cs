namespace Evidgate.BuildingBlocks.Observability.Redaction;

/// <summary>
/// An exception whose text has been through <see cref="SecretRedactor"/> (10.3.1-T1.4):
/// the message and the stack are the redacted originals, <see cref="ToString"/> renders
/// the redacted whole, and <see cref="OriginalTypeName"/> keeps the type the collector's
/// <c>exception.type</c> attribute reports, so a redacted exception is still triageable.
/// </summary>
/// <remarks>
/// A wrapper is the only total option: it needs no public <c>(string)</c> constructor on
/// the original type (which an arbitrary domain exception may not have), and it keeps the
/// stack text — a re-constructed same-type exception would have been thrown nowhere and
/// would carry <c>null</c> as its stack.
/// </remarks>
internal sealed class RedactedException : Exception
{
    private readonly string? _stackTrace;
    private readonly string _text;

    internal RedactedException(string originalTypeName, string message, string? stackTrace, string text)
        : base(message)
    {
        OriginalTypeName = originalTypeName;
        _stackTrace = stackTrace;
        _text = text;
    }

    /// <summary>The original exception's full type name — what <c>exception.type</c> must still say.</summary>
    public string OriginalTypeName { get; }

    /// <inheritdoc />
    public override string? StackTrace => _stackTrace;

    /// <inheritdoc />
    public override string ToString() => _text;
}
