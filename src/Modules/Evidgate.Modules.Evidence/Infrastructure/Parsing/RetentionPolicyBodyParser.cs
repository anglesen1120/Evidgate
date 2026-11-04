using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing;

/// <summary>Parses the retention route's bounded JSON body and validates its closed member set.</summary>
internal static class RetentionPolicyBodyParser
{
    /// <summary>The one <c>details[].path</c> an unreadable or over-long member name is reported under.</summary>
    private const string InvalidMemberNamePath = "/<invalid-member-name>";

    /// <summary>Longer member names answer the placeholder, so none is cut inside a surrogate pair.</summary>
    private const int MaxEchoedMemberName = 64;

    /// <summary>Parses bytes already bounded by the route, preserving its syntax and member-specific errors.</summary>
    internal static (RetentionPolicyBody Body, Error? Error) Parse(ReadOnlyMemory<byte> content)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return (default, Error.BadRequest("the body is not valid JSON"));
        }

        using (document)
        {
            return ReadMembers(document.RootElement);
        }
    }

    /// <summary>The member checks of the closed body, in the order a caller reads them.</summary>
    private static (RetentionPolicyBody Body, Error? Error) ReadMembers(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (default, Error.Invalid("the body must be a JSON object", [new ErrorDetail("$", "invalid_type")]));
        }

        var memberError = ValidateMembers(root);
        if (memberError is not null)
        {
            return (default, memberError);
        }

        var (artifactDays, artifactError) = ReadInt(root, "artifact_days");
        if (artifactError is not null)
        {
            return (default, artifactError);
        }

        var (metadataDays, metadataError) = ReadInt(root, "metadata_days");

        return metadataError is not null
            ? (default, metadataError)
            : (new RetentionPolicyBody(artifactDays!.Value, metadataDays!.Value), null);
    }

    /// <summary>
    /// The closed member set: an unknown member is refused by name, a name the reader cannot decode or cannot echo
    /// under the cap answers the stable placeholder, and a repeated member is refused like a malformed body — the
    /// last value would otherwise win silently.
    /// </summary>
    private static Error? ValidateMembers(JsonElement root)
    {
        try
        {
            return ScanMembers(root);
        }
        catch (InvalidOperationException)
        {
            // Enumerating a name that is not valid UTF-16 throws; echoing such a name would throw again while the
            // error envelope is serialised, so the answer carries the placeholder instead.
            return AdditionalMember(InvalidMemberNamePath);
        }
    }

    private static Error? ScanMembers(JsonElement root)
    {
        var seen = 0;
        foreach (var member in root.EnumerateObject())
        {
            var name = member.Name;
            var bit = name switch { "artifact_days" => 1, "metadata_days" => 2, _ => 0 };
            if (bit == 0)
            {
                return AdditionalMember(name.Length > MaxEchoedMemberName ? InvalidMemberNamePath : "/" + name);
            }

            if ((seen & bit) != 0)
            {
                return Error.Invalid(
                    "the body is not valid JSON",
                    [new ErrorDetail(string.Empty, "invalid_json")]);
            }

            seen |= bit;
        }

        return null;
    }

    private static Error AdditionalMember(string path) => Error.Invalid(
        "the body carries a member this route does not accept",
        [new ErrorDetail(path, "additional_properties")]);

    /// <summary>One integer member: absent is <c>required</c>, a non-integer is <c>invalid_type</c>.</summary>
    private static (int? Value, Error? Error) ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var member))
        {
            return (default, Error.Invalid("the request is missing a member", [new ErrorDetail(name, "required")]));
        }

        return member.ValueKind == JsonValueKind.Number && member.TryGetInt32(out var value)
            ? (value, null)
            : (default, Error.Invalid("a member has the wrong type", [new ErrorDetail(name, "invalid_type")]));
    }
}

/// <summary>The parsed pair, before the command applies the configured range.</summary>
/// <param name="ArtifactDays">The requested artifact window.</param>
/// <param name="MetadataDays">The requested metadata window.</param>
internal readonly record struct RetentionPolicyBody(int ArtifactDays, int MetadataDays);
