using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;

namespace Evidgate.BuildingBlocks.Pipeline.Behaviours;

/// <summary>
/// Tells one request fault apart from every other serialisation failure of a command. A body is parsed lazily, so a
/// <see cref="JsonElement"/> can carry an unpaired UTF-16 surrogate escape (<c>"\ud800"</c>) in a member name or string
/// that the parser accepted but the writer refuses to re-emit. Only that fault is the caller's: it is answered as a
/// bad request, while any other <see cref="JsonException"/> stays a server error.
/// <para>
/// The probe runs only after <c>JsonSerializer.Serialize</c> already failed, so a body that serialises today is never
/// touched and its stored request hash cannot change.
/// </para>
/// </summary>
internal static class UnstorableTextProbe
{
    private static readonly JsonSerializerOptions Options = Build();

    /// <summary>
    /// True when serialising <paramref name="message"/> as <paramref name="messageType"/> fails because a
    /// <see cref="JsonElement"/> in it holds an unpaired surrogate; false when the failure has another cause.
    /// </summary>
    internal static bool Explains(object message, Type messageType)
    {
        try
        {
            JsonSerializer.Serialize(message, messageType, Options);

            return false;
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
        var options = new JsonSerializerOptions();
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
            if (value.ValueKind != JsonValueKind.Undefined && UnstorableJsonText.HasUnstorableText(value))
            {
                throw new UnstorableTextFoundException();
            }

            value.WriteTo(writer);
        }
    }
}
