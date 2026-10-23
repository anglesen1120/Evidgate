using System.Text.Json;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// Payload (de)serialisation for task handlers (01.5.1 §5). Task payloads are
/// JSON documents on the wire contract's naming rules (00 §7: snake_case), so the
/// same payload text is readable next to an API document; a handler's
/// <c>TPayload</c> record declares PascalCase properties and is bound either way.
/// </summary>
internal static class TaskPayloadSerializer
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Renders a payload for an enqueue (schedules and their tests).</summary>
    internal static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, Options);

    /// <summary>
    /// Binds a stored payload to <typeparamref name="TPayload"/>. A malformed
    /// payload throws <see cref="JsonException"/>, which the consumer treats like
    /// any other handler failure: retried with backoff, then `dead_letter` with the
    /// redacted error — a producer's bad payload must be visible, not silently
    /// skipped.
    /// </summary>
    internal static TPayload Deserialize<TPayload>(string payloadJson) =>
        JsonSerializer.Deserialize<TPayload>(payloadJson, Options)!;
}
