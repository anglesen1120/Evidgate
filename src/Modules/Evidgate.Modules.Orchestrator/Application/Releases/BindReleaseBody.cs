using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Domain;

namespace Evidgate.Modules.Orchestrator.Application.Releases;

/// <summary>
/// The closed body of <c>POST /v1/releases/{id}/runs</c>: one member, <c>run_id</c>, a UUID. Bytes that are not JSON, a
/// member the route does not accept or a repeated member is 400/422 <c>request_invalid</c> — the shape every closed body
/// here uses — and the member rules are the create route's, so one body vocabulary serves both.
/// </summary>
internal static class BindReleaseBody
{
    /// <summary>The body's own members: the run to bind, and nothing else.</summary>
    private static readonly string[] TopLevelMembers = ["run_id"];

    /// <summary>Parses bytes already bounded by the route.</summary>
    internal static (Guid RunId, Error? Error) Parse(ReadOnlyMemory<byte> content)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return (Guid.Empty, Error.BadRequest("the body is not valid JSON", [new ErrorDetail(string.Empty, "invalid_json")]));
        }

        using (document)
        {
            var root = document.RootElement;
            if (JsonBody.HasUnstorableText(root))
            {
                return (Guid.Empty, Error.Invalid(
                    "the body carries a string that cannot be stored",
                    [new ErrorDetail(string.Empty, "invalid_value")]));
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return (Guid.Empty, Error.Invalid("the body must be a JSON object", [new ErrorDetail("$", "invalid_type")]));
            }

            if (ReleaseBody.Members(root, TopLevelMembers, null) is { } memberError)
            {
                return (Guid.Empty, memberError);
            }

            if (!root.TryGetProperty("run_id", out var runElement))
            {
                return (Guid.Empty, Error.Invalid("run_id is required", [new ErrorDetail("/run_id", "required")]));
            }

            return runElement.ValueKind == JsonValueKind.String
                && runElement.TryGetGuid(out var runId)
                && runId != Guid.Empty
                ? (runId, null)
                : (Guid.Empty, Error.Invalid("run_id must be a UUID", [new ErrorDetail("/run_id", "invalid_format")]));
        }
    }
}
