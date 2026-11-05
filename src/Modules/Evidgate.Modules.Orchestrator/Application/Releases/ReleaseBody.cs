using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Domain;

namespace Evidgate.Modules.Orchestrator.Application.Releases;

/// <summary>One component of a create-release body.</summary>
/// <param name="ComponentKey">The component's key inside its release: <c>[a-z0-9][a-z0-9_-]{0,63}</c>.</param>
/// <param name="Digest">The build digest the component was exercised at: <c>sha256:</c> + 64 lower-case hex.</param>
/// <param name="EnvironmentRevisionId">The environment revision the digest was built for, resolved inside the project.</param>
internal sealed record ReleaseComponentRequest(string ComponentKey, string Digest, Guid EnvironmentRevisionId);

/// <summary>One validated create-release body: a name, the state to store and at least one component.</summary>
internal sealed record ReleaseRequest(string Name, string State, IReadOnlyList<ReleaseComponentRequest> Components);

/// <summary>
/// The closed body of <c>POST /v1/projects/{p}/releases</c>, parsed once: bytes that are not JSON, a member the
/// route does not accept or a repeated member is 400 <c>request_invalid</c> — the shape every closed body uses; a body
/// of the declared shape that breaks a release rule is 422 <c>release_invalid</c> with the offending indices.
/// </summary>
internal static class ReleaseBody
{
    /// <summary>The body's own members, in the order a caller reads them.</summary>
    private static readonly string[] TopLevelMembers = ["name", "state", "components"];

    /// <summary>The members of one component.</summary>
    private static readonly string[] ComponentMembers = ["component_key", "digest", "environment_revision_id"];

    /// <summary>The one <c>details[].path</c> an unreadable or over-long member name is reported under.</summary>
    private const string InvalidMemberNamePath = "/<invalid-member-name>";

    /// <summary>Longer member names answer the placeholder, so none is cut inside a surrogate pair.</summary>
    private const int MaxEchoedMemberName = 64;

    /// <summary>Parses bytes already bounded by the route, preserving its syntax and member-specific errors.</summary>
    internal static (ReleaseRequest Request, Error? Error) Parse(ReadOnlyMemory<byte> content)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return (default!, Error.BadRequest("the body is not valid JSON", [new ErrorDetail(string.Empty, "invalid_json")]));
        }

        using (document)
        {
            if (JsonBody.HasUnstorableText(document.RootElement))
            {
                return (default!, Invalid("the body carries a string that cannot be stored", [new ErrorDetail(string.Empty, "invalid_value")]));
            }

            return ReadMembers(document.RootElement);
        }
    }

    /// <summary>The member checks of the closed body, in the order a caller reads them.</summary>
    private static (ReleaseRequest Request, Error? Error) ReadMembers(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (default!, Shape("the body must be a JSON object", [new ErrorDetail("$", "invalid_type")]));
        }

        if (Members(root, TopLevelMembers, null) is { } memberError)
        {
            return (default!, memberError);
        }

        if (ReadName(root) is not { } name)
        {
            return (default!, NameError(root));
        }

        if (root.TryGetProperty("state", out var stateElement) && stateElement.ValueKind != JsonValueKind.String)
        {
            return (default!, Shape("state is a string", [new ErrorDetail("/state", "invalid_type")]));
        }

        if (ReadState(root) is not { } state)
        {
            return (default!, Invalid("state must be \"draft\" or \"active\"", [new ErrorDetail("/state", "invalid_value")]));
        }

        if (!root.TryGetProperty("components", out var array))
        {
            return (default!, Invalid("a release needs at least one component", [new ErrorDetail("/components", "required")]));
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            return (default!, Shape("components is an array", [new ErrorDetail("/components", "invalid_type")]));
        }

        return ReadComponents(array, name, state);
    }

    /// <summary>The name, or null when it is missing, not a string, or outside the shared bound.</summary>
    private static string? ReadName(JsonElement root) =>
        root.TryGetProperty("name", out var element)
        && element.ValueKind == JsonValueKind.String
        && ReleaseRules.IsName(element.GetString())
            ? element.GetString()
            : null;

    /// <summary>The one detail a bad or absent name is reported with.</summary>
    private static Error NameError(JsonElement root) =>
        !root.TryGetProperty("name", out var element)
            ? Invalid("a release needs a name", [new ErrorDetail("/name", "required")])
            : element.ValueKind != JsonValueKind.String
                ? Shape("a release name is a string", [new ErrorDetail("/name", "invalid_type")])
                : Invalid(
                    $"a release name is 1..{ReleaseRules.MaxNameLength} trimmed printable characters",
                    [new ErrorDetail("/name", "invalid_value")]);

    /// <summary>The requested state, or null when the member is present and not one of the two declared values.</summary>
    private static string? ReadState(JsonElement root)
    {
        if (!root.TryGetProperty("state", out var element))
        {
            return ReleaseStates.Default;
        }

        return element.ValueKind == JsonValueKind.String && ReleaseStates.IsKnown(element.GetString())
            ? element.GetString()
            : null;
    }

    /// <summary>The array's bounds, then one pass over its items: a member error is 400 at once, shape errors collect.</summary>
    private static (ReleaseRequest Request, Error? Error) ReadComponents(JsonElement array, string name, string state)
    {
        var count = array.GetArrayLength();
        if (count == 0)
        {
            return (default!, Invalid("a release needs at least one component", [new ErrorDetail("/components", "empty")]));
        }

        if (count > ReleaseRules.MaxComponents)
        {
            return (default!, Invalid(
                $"a release declares at most {ReleaseRules.MaxComponents} components",
                [new ErrorDetail("/components", "too_many")]));
        }

        var components = new List<ReleaseComponentRequest>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var details = new List<ErrorDetail>();
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            // A component that is not a JSON object is a rule failure of the declared array, not a member scan: it is
            // collected with its index (`invalid_type`) like every other component-level refusal, so one bad item never
            // hides the rest and the answer stays `422 release_invalid`.
            if (item.ValueKind != JsonValueKind.Object)
            {
                details.Add(new ErrorDetail($"/components/{index}", "invalid_type", index));
                index++;
                continue;
            }

            if (Members(item, ComponentMembers, index) is { } memberError)
            {
                return (default!, memberError);
            }

            var (component, detail) = ReadComponent(item, index, seen);
            if (component is null)
            {
                details.Add(detail!);
            }
            else
            {
                components.Add(component);
            }

            index++;
        }

        return details.Count == 0
            ? (new ReleaseRequest(name, state, components), null)
            : (default!, Invalid("a component is not the declared shape", details));
    }

    /// <summary>One component of the array, or the first rule the item at <paramref name="index"/> breaks.</summary>
    private static (ReleaseComponentRequest? Component, ErrorDetail? Detail) ReadComponent(JsonElement item, int index, HashSet<string> seen)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return (null, new ErrorDetail($"/components/{index}", "invalid_type", index));
        }

        if (!item.TryGetProperty("component_key", out var keyElement))
        {
            return (null, new ErrorDetail($"/components/{index}/component_key", "required", index));
        }

        if (keyElement.ValueKind != JsonValueKind.String || !ReleaseRules.IsComponentKey(keyElement.GetString()))
        {
            return (null, new ErrorDetail($"/components/{index}/component_key", "invalid_value", index));
        }

        if (!seen.Add(keyElement.GetString()!))
        {
            return (null, new ErrorDetail($"/components/{index}/component_key", "duplicate", index));
        }

        if (!item.TryGetProperty("digest", out var digestElement))
        {
            return (null, new ErrorDetail($"/components/{index}/digest", "required", index));
        }

        if (digestElement.ValueKind != JsonValueKind.String || !DigestShape.IsSha256(digestElement.GetString()))
        {
            return (null, new ErrorDetail($"/components/{index}/digest", "invalid_value", index));
        }

        if (!item.TryGetProperty("environment_revision_id", out var revisionElement))
        {
            return (null, new ErrorDetail($"/components/{index}/environment_revision_id", "required", index));
        }

        return revisionElement.ValueKind == JsonValueKind.String && Guid.TryParse(revisionElement.GetString(), out var revisionId)
            ? (new ReleaseComponentRequest(keyElement.GetString()!, digestElement.GetString()!, revisionId), null)
            : (null, new ErrorDetail($"/components/{index}/environment_revision_id", "invalid_format", index));
    }

    /// <summary>
    /// The closed member set of one object: an unknown member is refused by name, a name the reader cannot decode or
    /// cannot echo under the cap answers the stable placeholder, and a repeated member is refused like a malformed body —
    /// the last value would otherwise win silently.
    /// </summary>
    internal static Error? Members(JsonElement root, ReadOnlySpan<string> allowed, int? index)
    {
        try
        {
            return ScanMembers(root, allowed, index);
        }
        catch (InvalidOperationException)
        {
            return AdditionalMember(InvalidMemberNamePath, index);
        }
    }

    private static Error? ScanMembers(JsonElement root, ReadOnlySpan<string> allowed, int? index)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in root.EnumerateObject())
        {
            if (!allowed.Contains(member.Name))
            {
                return AdditionalMember(member.Name.Length > MaxEchoedMemberName ? InvalidMemberNamePath : "/" + member.Name, index);
            }

            if (!seen.Add(member.Name))
            {
                return Shape("the body repeats a member", [new ErrorDetail(string.Empty, "invalid_json")]);
            }
        }

        return null;
    }

    private static Error AdditionalMember(string path, int? index) => Shape(
        "the body carries a member this route does not accept",
        [new ErrorDetail(path, "additional_properties", index)]);

    /// <summary>
    /// 422 <c>request_invalid</c>: the body is JSON but not the declared shape — a root that is not an object, a
    /// member this route does not accept or repeats, or a declared member of the wrong JSON type. The
    /// <c>RetentionPolicyBodyParser</c> precedent answers exactly these with <c>Error.Invalid</c>, and only
    /// unparseable bytes with a 400.
    /// </summary>
    private static Error Shape(string message, IReadOnlyList<ErrorDetail> details) =>
        Error.Invalid(message, details);

    /// <summary>422 <c>release_invalid</c> with the parse's own details: a body of the declared shape breaking a release rule.</summary>
    private static Error Invalid(string message, IReadOnlyList<ErrorDetail> details) =>
        Error.Unprocessable(ApiErrorCode.ReleaseInvalid, message, details);
}
