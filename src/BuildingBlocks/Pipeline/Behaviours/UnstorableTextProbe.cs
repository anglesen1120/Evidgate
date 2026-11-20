using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;

namespace Evidgate.BuildingBlocks.Pipeline.Behaviours;

/// <summary>
/// Tells the two request faults of a command serialisation apart from every other failure: a <see cref="JsonElement"/> the
/// parser accepted but the writer cannot re-emit (an unpaired surrogate), and nesting the command wrapper pushes past the
/// writer's default depth limit. Both are the caller's and are answered as a bad request; any other
/// <see cref="JsonException"/> stays a server error.
/// </summary>
internal static class UnstorableTextProbe
{
    /// <summary>
    /// The probe's depth bound: a request body parses up to depth 64, and the command wrapper adds the levels above it,
    /// so 128 covers the wrapper shift while a cycle or a genuinely wrong shape still fails here.
    /// </summary>
    private const int ProbeMaxDepth = 128;

    private static readonly JsonSerializerOptions Options = Build();

    /// <summary>
    /// True when one re-serialisation explains the original failure: the marker for unpaired surrogate text, or success,
    /// which means the default writer's depth limit was the only difference. This runs only after that serialisation
    /// already failed, so a cycle or any other cause still throws <see cref="JsonException"/> and answers false.
    /// </summary>
    internal static bool Explains(object message, Type messageType)
    {
        try
        {
            JsonSerializer.Serialize(message, messageType, Options);

            return true;
        }
        catch (UnstorableTextFoundException)
        {
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions { MaxDepth = ProbeMaxDepth };
        options.Converters.Add(new CheckedElementConverter());

        return options;
    }

    private sealed class UnstorableTextFoundException : Exception;

    private sealed class CheckedElementConverter : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("the probe only writes");

        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
        {
            if (value.ValueKind != JsonValueKind.Undefined && UnstorableJsonText.HasUnpairedSurrogate(value))
            {
                throw new UnstorableTextFoundException();
            }

            value.WriteTo(writer);
        }
    }
}
