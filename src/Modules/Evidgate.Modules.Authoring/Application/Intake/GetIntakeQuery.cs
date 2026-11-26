using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>Reads one intake of a project (route 2).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="IntakeId">The intake from the route.</param>
internal sealed record GetIntakeQuery(Guid ProjectId, Guid IntakeId) : IQuery<Result<IntakeStatusResponse>>;

/// <summary>Handler of <see cref="GetIntakeQuery"/>.</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, for the project scope check.</param>
/// <param name="tenant">The request's tenant context.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class GetIntakeQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant) : IQueryHandler<GetIntakeQuery, Result<IntakeStatusResponse>>
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public async ValueTask<Result<IntakeStatusResponse>> Handle(GetIntakeQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(GetIntakeQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<IntakeStatusResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;
        var row = await db.QuerySingleOrDefaultAsync<IntakeStatusRow>(
            AuthoringIntakeSql.Status,
            new { org_id = orgId, project_id = query.ProjectId, id = query.IntakeId },
            cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            return Result.Failure<IntakeStatusResponse>(AuthoringErrors.NotFound("no intake with that id is visible in this project"));
        }

        var eta = row.State is "received" or "extracting"
            ? await EtaClassAsync(orgId, row, cancellationToken).ConfigureAwait(false)
            : null;

        return Result.Success(new IntakeStatusResponse(
            row.Id,
            row.Kind,
            row.State,
            eta,
            row.FailureCode,
            row.ExtractedTextHash,
            row.ExtractedCharCount,
            row.ArtifactId,
            CapsElement(row.Caps),
            row.Version));
    }

    private static JsonElement CapsElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// <c>short</c> when the intake's queue position is expected to clear within a minute, <c>long</c> otherwise: with
    /// <c>s</c> slots and an extraction budget of <c>t</c>, an intake with <c>a</c> unfinished intakes ahead of it waits
    /// at most <c>(a / s + 1) · t</c>. The position is counted only up to the point where the answer is already
    /// <c>long</c>.
    /// </summary>
    private async Task<string> EtaClassAsync(Guid orgId, IntakeStatusRow row, CancellationToken cancellationToken)
    {
        if (IntakeCapsJson.Deserialize(row.Caps) is not { } stored || stored.MaxConcurrentExtractionsPerTenant < 1)
        {
            return "long";
        }

        var waves = (int)(ShortWait / stored.ExtractionTimeout);
        var bound = waves * stored.MaxConcurrentExtractionsPerTenant;
        if (bound < 1)
        {
            return "long";
        }

        var ahead = await db.ScalarAsync<int>(
            AuthoringIntakeSql.QueueAhead,
            new { org_id = orgId, created_at = row.CreatedAt, id = row.Id, bound },
            cancellationToken).ConfigureAwait(false);
        return ahead < bound ? "short" : "long";
    }
}

/// <summary>One <c>authoring_intake</c> row as the status read selects it.</summary>
internal sealed class IntakeStatusRow
{
    /// <summary>The intake id.</summary>
    public Guid Id { get; set; }

    /// <summary>The kind.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The state.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The closed failure code of a rejected intake.</summary>
    public string? FailureCode { get; set; }

    /// <summary>The digest of the extracted text.</summary>
    public string? ExtractedTextHash { get; set; }

    /// <summary>Code points of the extracted text.</summary>
    public int? ExtractedCharCount { get; set; }

    /// <summary>The stored artifact.</summary>
    public Guid? ArtifactId { get; set; }

    /// <summary>The caps document.</summary>
    public string Caps { get; set; } = "{}";

    /// <summary>The row version.</summary>
    public int Version { get; set; }

    /// <summary>The creation instant; with the id it orders the queue.</summary>
    public DateTime CreatedAt { get; set; }
}
