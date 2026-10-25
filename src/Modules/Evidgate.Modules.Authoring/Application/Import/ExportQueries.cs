using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Application.Import;

/// <summary>
/// Exports one project's feature tree, or one of its subtrees, as the flat import table of 13.1.1 §5 route 25 (AC14).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only by construction.</b> The message is an <see cref="IQuery{TResponse}"/>, so the C2 tenant stage opens a
/// read-only transaction (C1/C5) and the exporter's statements run inside it; the route writes no audit row, because an
/// export changes nothing.
/// </para>
/// <para>
/// <b>The file is built after the read.</b> The exporter materialises at most <c>ImportLimits.MaxDataRows</c> rows and the
/// handler serialises them into a buffered stream with the writer of <paramref name="Format"/>, so the answer is one
/// <see cref="IResult"/> the host's <c>ResultEndpointFilter</c> passes through: a successful export is the file, and a
/// refused one (404 <c>not_found</c>, 413 <c>payload_too_large</c>) is the 00 §9.2 envelope, never a truncated download.
/// Nothing is persisted and nothing is cached (<c>Cache-Control: no-store</c> is the delegate's header).
/// </para>
/// </remarks>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Format">The normalized format token: <c>csv</c>, <c>json</c> or <c>xlsx</c>.</param>
/// <param name="NodeId">The subtree root, or null for the whole project.</param>
/// <param name="Include">The kinds of row to emit (default all three).</param>
internal sealed record ExportAuthoringQuery(Guid ProjectId, string Format, Guid? NodeId, ImportInclude Include)
    : IQuery<Result<IResult>>;

/// <summary>Handler of <see cref="ExportAuthoringQuery"/>: read the rows, then serialise them in the requested format.</summary>
/// <param name="scopes">The open (read-only) tenant scope.</param>
/// <param name="principals">The caller, for the C2 project-scope rule.</param>
/// <param name="tenant">The request's tenant context, for the C2 project-scope rule.</param>
/// <param name="exporter">The exporter of §5 route 25 (13.1.1 AC14).</param>
/// <remarks>
/// The 404 of an invisible project or an unknown <c>node_id</c>, and the 413 of a scope over the row cap, are the
/// exporter's own errors and pass through unchanged — the handler adds the C2 project-scope refusal in front of them,
/// exactly like the read routes that address a project.
/// </remarks>
[Permission(Permissions.Authoring.Read)]
internal sealed class ExportAuthoringQueryHandler(
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    AuthoringExporter exporter) : IQueryHandler<ExportAuthoringQuery, Result<IResult>>
{
    /// <inheritdoc />
    public async ValueTask<Result<IResult>> Handle(
        ExportAuthoringQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ExportAuthoringQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<IResult>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        var exported = await exporter.ExportAsync(
            orgId,
            query.ProjectId,
            query.NodeId,
            query.Include,
            cancellationToken).ConfigureAwait(false);

        if (!exported.IsSuccess)
        {
            return Result.Failure<IResult>(exported.Error!);
        }

        // At most ImportLimits.MaxDataRows rows, so the file is a bounded buffer: the writer's output is buffered rather
        // than streamed straight to the response, because a writer that threw mid-file would otherwise have already sent
        // a 200 and half a file (the exporter's row cap is what makes the buffer safely bounded).
        var file = new MemoryStream();
        await ImportFormats.WriteAsync(query.Format, exported.Value!, file, cancellationToken).ConfigureAwait(false);
        file.Position = 0;

        return Result.Success<IResult>(Results.Stream(
            file,
            ImportFormats.ContentType(query.Format),
            ImportFormats.FileName(query.Format)));
    }
}
