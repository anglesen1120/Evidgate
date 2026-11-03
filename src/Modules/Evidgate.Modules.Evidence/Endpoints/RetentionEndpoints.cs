using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Application.Retention;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Evidence.Endpoints;

/// <summary>
/// The retention routes of the evidence area: one settings row per project, read and written as a whole object. The
/// body is read here, under a small cap and with a closed member set, because the route's contract refuses an unknown
/// member by name (<c>additional_properties</c>) and a missing or mistyped one with its own reason — neither of which a
/// model binding failure can express. Authorization, the tenant transaction and the envelope stay the pipeline's and the
/// host's.
/// </summary>
public static class RetentionEndpoints
{
    /// <summary>The route body is two integers; the cap is generous and still bounded before parsing.</summary>
    private const int MaxBodyBytes = 4 * 1024;

    /// <summary>The closed member set of the <c>PUT</c> body.</summary>
    private static readonly string[] BodyMembers = ["artifact_days", "metadata_days"];

    /// <summary>Maps the routes and returns the <c>/v1</c> group so the host can apply its filters.</summary>
    public static RouteGroupBuilder MapRetentionEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("retention");

        group.MapGet("/projects/{p}/retention", GetRetentionPolicyAsync)
            .Produces<RetentionPolicy>(StatusCodes.Status200OK)
            .WithSummary("Read a project's evidence retention window (defaults when unset)");

        group.MapPut("/projects/{p}/retention", SetRetentionPolicyAsync)
            .Produces<RetentionPolicy>(StatusCodes.Status200OK)
            .WithSummary("Set a project's evidence retention window within the configured range");

        return group;
    }

    private static ValueTask<Result<RetentionPolicy>> GetRetentionPolicyAsync(
        Guid projectId,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        mediator.Send(new GetRetentionPolicyQuery(projectId), cancellationToken);

    private static async Task<Result<RetentionPolicy>> SetRetentionPolicyAsync(
        Guid projectId,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var (body, error) = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);

        return error is not null
            ? Result.Failure<RetentionPolicy>(error)
            : await mediator.Send(
                new SetRetentionPolicyCommand(projectId, body.ArtifactDays, body.MetadataDays),
                cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the closed body: an object of exactly <c>artifact_days</c> and <c>metadata_days</c>, both integers. The
    /// cap is applied to the bytes as they arrive, so an oversized body never reaches the parser.
    /// </summary>
    private static async Task<(RetentionPolicyBody Body, Error? Error)> ReadBodyAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxBodyBytes + 1];
        var read = 0;

        while (read < buffer.Length)
        {
            var taken = await context.Request.Body.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (taken == 0)
            {
                break;
            }

            read += taken;
        }

        if (read > MaxBodyBytes)
        {
            return (default, new Error(
                ApiErrorCode.PayloadTooLarge,
                "the request body is too large",
                StatusCodes.Status413PayloadTooLarge));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(buffer.AsMemory(0, read));
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

        foreach (var member in root.EnumerateObject())
        {
            if (!BodyMembers.Contains(member.Name, StringComparer.Ordinal))
            {
                var shown = member.Name.Length > 64 ? member.Name[..64] : member.Name;

                return (default, Error.Invalid(
                    "the body carries a member this route does not accept",
                    [new ErrorDetail("/" + shown, "additional_properties")]));
            }
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

    /// <summary>The parsed pair, before the command applies the configured range.</summary>
    /// <param name="ArtifactDays">The requested artifact window.</param>
    /// <param name="MetadataDays">The requested metadata window.</param>
    private readonly record struct RetentionPolicyBody(int ArtifactDays, int MetadataDays);
}

/// <summary>
/// 200 body of both retention routes: the stored windows, or the platform defaults when the project has no row. The
/// two members are the whole object — the row's <c>version</c> is carried for the database naming contract and is not
/// exposed, because this route has no conditional write.
/// </summary>
/// <param name="ArtifactDays">How long artifact bytes are kept, in days.</param>
/// <param name="MetadataDays">How long the artifact's metadata is kept, in days.</param>
public sealed record RetentionPolicy(
    [property: JsonPropertyName("artifact_days")] int ArtifactDays,
    [property: JsonPropertyName("metadata_days")] int MetadataDays);
