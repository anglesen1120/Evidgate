namespace Evidgate.BuildingBlocks.Canonicalization;

/// <summary>
/// A JSON document cannot be expressed in RFC 8785 canonical form: duplicate
/// property names, lone surrogates, or numbers outside the IEEE 754 double range
/// (RFC 8785 §3.2.2.2/§3.2.2.3). Invalid JSON text itself surfaces as
/// <see cref="System.Text.Json.JsonException"/> from the reader.
/// </summary>
public sealed class JsonCanonicalizationException : Exception
{
    /// <summary>Creates the exception for a document that violates RFC 8785.</summary>
    public JsonCanonicalizationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception for a document that violates RFC 8785, with the underlying cause.</summary>
    public JsonCanonicalizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
