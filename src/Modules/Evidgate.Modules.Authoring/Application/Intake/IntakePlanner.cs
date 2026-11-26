using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>What a request resolved to once every refusal has been ruled out; nothing is written before this exists.</summary>
/// <param name="Kind">One of <c>text</c>, <c>docx</c>, <c>pdf</c>, <c>image</c>.</param>
/// <param name="NodeId">The target feature node; null for an image.</param>
/// <param name="ArtifactId">The stored artifact; null for text.</param>
/// <param name="InputText">The NFC text of a text intake.</param>
/// <param name="MediaType">The media type the row stores.</param>
/// <param name="DeclaredBytes">The byte count the row stores.</param>
internal sealed record IntakePlan(string Kind, Guid? NodeId, Guid? ArtifactId, string? InputText, string MediaType, long DeclaredBytes);

/// <summary>
/// Judges an intake request before the row exists: the body against its schema, then the kind's own rules, the target
/// node and the artifact. A refusal here writes nothing and starts no job.
/// </summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="artifacts">The Evidence module's view of a ready artifact and its effective kind policy.</param>
/// <param name="schemaSet">The compiled contract schemas.</param>
internal sealed class IntakePlanner(ITenantDb db, IIntakeArtifactReader artifacts, Lazy<ContractSchemaSet> schemaSet)
{
    private const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string Pdf = "application/pdf";

    private static readonly string[] Images = ["image/png", "image/jpeg", "image/webp"];

    /// <summary>Resolves a request body into a plan, or the refusal that stops it.</summary>
    /// <param name="orgId">The caller's tenant.</param>
    /// <param name="projectId">The project of the route.</param>
    /// <param name="body">The parsed body.</param>
    /// <param name="caps">The caps in force.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<(IntakePlan? Plan, Error? Error)> PlanAsync(
        Guid orgId,
        Guid projectId,
        JsonElement body,
        IntakeCaps caps,
        CancellationToken cancellationToken)
    {
        var verdict = schemaSet.Value.Validate(ContractSchemaKeys.AuthoringGenerationIntake, body);
        if (!verdict.IsValid)
        {
            return (null, Error.Invalid(
                "the body does not match contracts/schemas/authoring-generation-intake.v1.json",
                [.. verdict.Errors.Select(error => new ErrorDetail(error.Path, error.Reason))]));
        }

        var kind = body.GetProperty("kind").GetString()!;
        var nodeId = Member(body, "feature_node_id") is { } node ? Guid.Parse(node) : (Guid?)null;

        if (nodeId is { } target && await db.ScalarAsync<int?>(
                AuthoringIntakeSql.LiveNode,
                new { org_id = orgId, project_id = projectId, id = target },
                cancellationToken).ConfigureAwait(false) is null)
        {
            return (null, Error.NotFound("no feature node with that id is visible in this project"));
        }

        if (kind != "text")
        {
            return await PlanArtifactAsync(orgId, projectId, body, kind, nodeId, caps, cancellationToken).ConfigureAwait(false);
        }

        var refusal = IntakeText.Check(Member(body, "text") ?? string.Empty, caps.MaxTextCodePoints, out var text);
        return refusal is not null
            ? (null, refusal)
            : (new IntakePlan(kind, nodeId, null, text, "text/plain", Encoding.UTF8.GetByteCount(text)), null);
    }

    private async Task<(IntakePlan? Plan, Error? Error)> PlanArtifactAsync(
        Guid orgId,
        Guid projectId,
        JsonElement body,
        string kind,
        Guid? nodeId,
        IntakeCaps caps,
        CancellationToken cancellationToken)
    {
        var artifactId = Guid.Parse(Member(body, "artifact_id")!);
        string[] allowed = kind switch { "docx" => [Docx], "pdf" => [Pdf], _ => Images };
        var expectedKind = kind == "image" ? ArtifactKinds.RequirementImage : ArtifactKinds.RequirementDocument;

        if (Member(body, "media_type") is { } declared && !MediaTypes.IsAllowed(allowed, declared))
        {
            return (null, Refuse("/media_type", "media_type_not_allowed", "the media type is not allowed for this kind"));
        }

        var artifact = await artifacts.ReadEligibleAsync(orgId, projectId, artifactId, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            return (null, Error.NotFound("no ready artifact with that id is visible in this project"));
        }

        // The row stores the accepted type in its own spelling, never the artifact's declared one.
        var mediaType = MediaTypes.Match(allowed, artifact.MediaType);
        if (artifact.Kind != expectedKind || !artifact.MediaAllowed || mediaType is null)
        {
            return (null, Refuse("/artifact_id", "media_type_not_allowed", "the artifact is not an allowed input for this kind"));
        }

        var limit = Math.Min(artifact.MaxBytes, kind == "image" ? caps.MaxImageBytes : caps.MaxDocumentBytes);
        var requested = !body.TryGetProperty("bytes", out var bytes) ? 0 : bytes.TryGetInt64(out var value) ? value : long.MaxValue;
        if (artifact.DeclaredBytes > limit || requested > limit)
        {
            return (null, Refuse("/bytes", "size_limit_exceeded", $"the declared size is above the {limit} byte limit"));
        }

        if (artifact.DeclaredBytes < 1)
        {
            return (null, Refuse("/artifact_id", "empty_input", "the artifact holds no bytes"));
        }

        var existing = await db.ScalarAsync<Guid?>(
            AuthoringIntakeSql.ByArtifact, new { org_id = orgId, artifact_id = artifactId }, cancellationToken).ConfigureAwait(false);
        return existing is not null
            ? (null, Error.Conflict(ApiErrorCode.IdempotencyConflict, "an intake already exists for this artifact"))
            : (new IntakePlan(kind, nodeId, artifactId, null, mediaType, artifact.DeclaredBytes), null);
    }

    private static string? Member(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Error Refuse(string path, string reason, string message) => Error.Invalid(message, [new ErrorDetail(path, reason)]);
}
