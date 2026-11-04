using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;

namespace Evidgate.Modules.Catalog.Infrastructure.Persistence;

/// <summary>A <c>test_case</c> row as Dapper reads it: <c>text[]</c> arrives as <c>string[]</c>, so this is a class, not the DTO record.</summary>
internal class CaseRow
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid AnnotationId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Framework { get; set; } = string.Empty;

    public string? Language { get; set; }

    public string? FilePath { get; set; }

    public string Status { get; set; } = string.Empty;

    public Guid? OwnerMembershipId { get; set; }

    public string[] Labels { get; set; } = [];

    public string? RequirementRef { get; set; }

    public string FirstSeenCommit { get; set; } = string.Empty;

    public string? LastSeenCommit { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Version { get; set; }

    public CaseDto ToDto() => new(
        Id, OrgId, ProjectId, AnnotationId, Name, Framework, Language, FilePath, Status,
        OwnerMembershipId, Labels, FirstSeenCommit, LastSeenCommit, CreatedAt, UpdatedAt, Version, RequirementRef);
}

/// <summary><see cref="CaseRow"/> plus the two JSON trails of <see cref="CatalogReadSql.CaseDetail"/>.</summary>
internal sealed class CaseDetailRow : CaseRow
{
    public string SourcesJson { get; set; } = "[]";

    public string RenamesJson { get; set; } = "[]";
}

/// <summary>The Dapper read-back of one case that the queries and the PATCH response share, so a command answers exactly what the GET returns.</summary>
internal static class CaseReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>The case visible to the ambient tenant, or null (another org's id included).</summary>
    internal static async Task<CaseDto?> CaseAsync(ITenantDb db, Guid caseId, CancellationToken cancellationToken)
    {
        var row = await db.QuerySingleOrDefaultAsync<CaseRow>(CatalogReadSql.CaseById, new { case_id = caseId }, cancellationToken)
            .ConfigureAwait(false);

        return row?.ToDto();
    }

    /// <summary>The case with its identity trail, or null.</summary>
    internal static async Task<CaseDetailDto?> DetailAsync(ITenantDb db, Guid caseId, CancellationToken cancellationToken)
    {
        var row = await db.QuerySingleOrDefaultAsync<CaseDetailRow>(CatalogReadSql.CaseDetail, new { case_id = caseId }, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        return new CaseDetailDto(
            row.ToDto(),
            JsonSerializer.Deserialize<List<CaseIdentitySourceDto>>(row.SourcesJson, Json) ?? [],
            JsonSerializer.Deserialize<List<CaseRenameDto>>(row.RenamesJson, Json) ?? []);
    }
}
