using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Application.Queries;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Authoring.Endpoints;

/// <summary>
/// The five project AI settings routes of the versioned settings story: the effective document and its save,
/// the version timeline and one version, and the closed template catalogue.
/// </summary>
/// <remarks>
/// <para>
/// They sit under <c>/v1/projects/{p}/ai-settings</c>, not under the authoring tree, so this file maps its own
/// group on the <c>/v1</c> group the module returns. Like the authoring routes the delegates only bind 00 §9.1's
/// parameters and answer in <see cref="Result"/> terms: authorization is the C2 pipeline, the tenant is the
/// request's, and the envelope is the host's <c>ResultEndpointFilter</c>.
/// </para>
/// <para>
/// Every answer of the two document routes carries the version <c>ETag</c>, and the save requires it as
/// <c>If-Match</c>: the pair is what makes a lost update a 412 with the current version instead of a silent
/// overwrite of a concurrent author's version.
/// </para>
/// </remarks>
internal static class AiSettingsEndpoints
{
    /// <summary>Maps the settings routes onto the module's <c>/v1</c> group.</summary>
    /// <param name="routes">The <c>/v1</c> group the authoring module already created.</param>
    internal static void MapAiSettingsEndpoints(RouteGroupBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var settings = routes.MapGroup("/projects/{p}/ai-settings").WithTags("ai-settings");

        settings.MapGet(string.Empty, GetAsync)
            .Produces<AiSettingsView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("The effective project AI settings and their version ETag");

        settings.MapPut(string.Empty, SaveAsync)
            .Produces<AiSettingsView>(StatusCodes.Status201Created)
            .Produces<AiSettingsView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Append one AI settings version (If-Match + Idempotency-Key)");

        settings.MapGet("/versions", ListVersionsAsync)
            .Produces<AiSettingsVersionListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Keyset page of the settings versions, newest first");

        settings.MapGet("/versions/{version_no:int}", GetVersionAsync)
            .Produces<AiSettingsVersion>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("One stored settings version with its document");

        settings.MapGet("/templates", ListTemplatesAsync)
            .Produces<AiSettingsTemplateListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("The platform's closed AI settings template catalogue");
    }

    /// <summary>The effective document, with the version <c>ETag</c> the save's <c>If-Match</c> expects.</summary>
    private static async Task<Result<AiSettingsView>> GetAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = await mediator.Send(new GetAiSettingsQuery(p), cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            AiSettingsPayload.SetVersionETag(context, result.Value.VersionNo);
        }

        return result;
    }

    /// <summary>Route 5's save: the read version, the request body, then the command's ordered decisions.</summary>
    private static async Task<Result<AiSettingsView>> SaveAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (request, payloadError) = await AiSettingsPayload.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        if (payloadError is not null || request is null)
        {
            return Result.Failure<AiSettingsView>(payloadError!);
        }

        var result = await mediator.Send(
            new SaveAiSettingsCommand(
                p,
                request.Settings,
                request.TemplateCode,
                request.TemplateVersion,
                request.Reason,
                request.Version,
                context.Request.Headers["Idempotency-Key"].ToString()),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            AiSettingsPayload.SetVersionETag(context, result.Value.VersionNo);
        }

        return result;
    }

    /// <summary>The newest-first timeline page; its cursor is the handler's to refuse.</summary>
    private static async Task<Result<AiSettingsVersionListResponse>> ListVersionsAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (AuthoringEndpointSupport.LimitError(context.Request.Query["limit"], out var limit) is { } limitError)
        {
            return Result.Failure<AiSettingsVersionListResponse>(limitError);
        }

        return await mediator.Send(
            new ListAiSettingsVersionsQuery(p, AuthoringEndpointText.Blank(context.Request.Query["cursor"]), limit),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One version by number; the number is the whole address, so the handler decides the 404.</summary>
    private static Task<Result<AiSettingsVersion>> GetVersionAsync(
        Guid p,
        int version_no,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        mediator.Send(new GetAiSettingsVersionQuery(p, version_no), cancellationToken).AsTask();

    /// <summary>The template catalogue, scoped through its project so a foreign project stays a 404.</summary>
    private static Task<Result<AiSettingsTemplateListResponse>> ListTemplatesAsync(
        Guid p,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        mediator.Send(new ListAiSettingsTemplatesQuery(p), cancellationToken).AsTask();
}
