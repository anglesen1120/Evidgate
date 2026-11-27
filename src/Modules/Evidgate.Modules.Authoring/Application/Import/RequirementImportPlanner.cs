using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application.Import;

/// <summary>One requirement row resolved against the project: what it will do and which stored row it names.</summary>
/// <param name="Row">The validated row.</param>
/// <param name="Outcome">Create, Update or Unchanged (a requirement import never restores).</param>
/// <param name="Existing">The stored requirement the row names; null for a create.</param>
/// <param name="Identity">The identity after the import; its parent is null while <paramref name="ParentIsNew"/> holds.</param>
/// <param name="ParentIsNew">True when the parent is a row of this file that is being created, so its id exists only at apply time.</param>
internal sealed record RequirementPlannedRow(
    RequirementImportRow Row,
    ImportOutcome Outcome,
    RequirementImportSql.StoredRequirement? Existing,
    RequirementIdentity Identity,
    bool ParentIsNew);

/// <summary>The resolution of a whole file: the planned rows, or the issues that refuse it.</summary>
/// <param name="Rows">One entry per input row, in input order.</param>
/// <param name="Issues">The refusals; empty when the plan is accepted.</param>
internal sealed record RequirementPlan(IReadOnlyList<RequirementPlannedRow> Rows, IReadOnlyList<ImportIssue> Issues)
{
    /// <summary>True when nothing refuses the file.</summary>
    public bool IsAccepted => Issues.Count == 0;
}

/// <summary>
/// Resolves a validated requirement file against the project with two reads, whatever the row count: the live feature
/// nodes the rows link to, and the live requirements the rows can name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity.</b> A row with a <c>code</c> names the live requirement of the project with that external reference. A
/// row without one names the live requirement of the same kind and exact title under the same parent; a row whose
/// parent is created by this file has no stored match. Two stored candidates, or two rows naming one stored requirement,
/// are <c>ambiguous_match</c>: the importer never picks one.
/// </para>
/// <para>
/// <b>What a blank cell means.</b> The hierarchy and the text follow the file; a blank <c>code</c> or <c>item_key</c> keeps
/// the stored reference or link, so re-importing a plain outline never unlinks what an editor linked.
/// </para>
/// </remarks>
internal sealed class RequirementImportPlanner(ITenantDb db)
{
    /// <summary>Resolves every row, or collects the reasons the file is refused.</summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="rows">The validated rows, in file order.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<RequirementPlan> PlanAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyList<RequirementImportRow> rows,
        CancellationToken cancellationToken)
    {
        var issues = new List<ImportIssue>();
        await RefuseUnknownFeatureNodesAsync(orgId, projectId, rows, issues, cancellationToken).ConfigureAwait(false);
        var stored = await StoredAsync(orgId, projectId, rows, cancellationToken).ConfigureAwait(false);

        return new RequirementPlan(Resolve(rows, stored, issues), issues);
    }

    /// <summary>A feature node of another project, a deleted one and a missing one answer the same reason, with no detail.</summary>
    private async Task RefuseUnknownFeatureNodesAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyList<RequirementImportRow> rows,
        List<ImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var wanted = rows.Where(row => row.FeatureNodeId is not null)
            .Select(row => row.FeatureNodeId!.Value.ToString("D"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (wanted.Length == 0)
        {
            return;
        }

        var live = await db.QueryAsync<Guid>(
            RequirementImportSql.LiveFeatureNodes,
            new { org_id = orgId, project_id = projectId, ids = new ImportSql.TextArrayParameter(wanted) },
            cancellationToken).ConfigureAwait(false);
        var liveSet = live.ToHashSet();

        foreach (var row in rows.Where(row => row.FeatureNodeId is { } node && !liveSet.Contains(node)))
        {
            ImportSchema.Add(issues, row.Source.Index, "/item_key", "unknown_feature_node");
        }
    }

    private async Task<IReadOnlyList<RequirementImportSql.StoredRequirement>> StoredAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyList<RequirementImportRow> rows,
        CancellationToken cancellationToken)
    {
        var refs = rows.Where(row => row.ExternalRef is not null).Select(row => row.ExternalRef!).Distinct(StringComparer.Ordinal).ToArray();
        var titles = rows.Where(row => row.ExternalRef is null).Select(row => row.Title).Distinct(StringComparer.Ordinal).ToArray();

        return await db.QueryAsync<RequirementImportSql.StoredRequirement>(
            RequirementImportSql.Candidates,
            new
            {
                org_id = orgId,
                project_id = projectId,
                refs = new ImportSql.TextArrayParameter(refs),
                titles = new ImportSql.TextArrayParameter(titles),
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static List<RequirementPlannedRow> Resolve(
        IReadOnlyList<RequirementImportRow> rows,
        IReadOnlyList<RequirementImportSql.StoredRequirement> stored,
        List<ImportIssue> issues)
    {
        var byReference = stored.Where(entry => entry.ExternalRef is not null).ToLookup(entry => entry.ExternalRef!, StringComparer.Ordinal);
        var byTitle = stored.ToLookup(entry => entry.Title, StringComparer.Ordinal);
        var resolvedIds = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        var claimed = new HashSet<Guid>();
        var planned = new List<RequirementPlannedRow>(rows.Count);

        foreach (var row in rows)
        {
            Guid? parentId = null;
            if (row.ParentKey is { } parentKey)
            {
                parentId = resolvedIds[parentKey];
            }

            var parentIsNew = row.ParentKey is not null && parentId is null;
            var candidates = Candidates(row, byReference, byTitle, parentId, parentIsNew);
            if (candidates.Count > 1 || (candidates.Count == 1 && !claimed.Add(candidates[0].Id)))
            {
                ImportSchema.Add(issues, row.Source.Index, row.ExternalRef is null ? "/title" : "/code", "ambiguous_match");
                resolvedIds[row.Source.Key] = null;

                continue;
            }

            var match = candidates.Count == 1 ? candidates[0] : null;
            planned.Add(Plan(row, match, parentId, parentIsNew));
            resolvedIds[row.Source.Key] = match?.Id;
        }

        return planned;
    }

    private static List<RequirementImportSql.StoredRequirement> Candidates(
        RequirementImportRow row,
        ILookup<string, RequirementImportSql.StoredRequirement> byReference,
        ILookup<string, RequirementImportSql.StoredRequirement> byTitle,
        Guid? parentId,
        bool parentIsNew)
    {
        if (row.ExternalRef is { } reference)
        {
            return [.. byReference[reference]];
        }

        return parentIsNew
            ? []
            : [.. byTitle[row.Title].Where(entry => entry.Kind == row.Kind && entry.ParentRequirementId == parentId)];
    }

    private static RequirementPlannedRow Plan(
        RequirementImportRow row,
        RequirementImportSql.StoredRequirement? match,
        Guid? parentId,
        bool parentIsNew)
    {
        var identity = new RequirementIdentity(
            row.Kind,
            row.Title,
            row.ExternalRef ?? match?.ExternalRef,
            row.FeatureNodeId ?? match?.FeatureNodeId,
            parentId);

        if (match is null)
        {
            return new RequirementPlannedRow(row, ImportOutcome.Create, null, identity, parentIsNew);
        }

        // An unchanged row writes nothing: no version, no audit row. A parent created by this file is never the stored one.
        var same = !parentIsNew
            && identity == new RequirementIdentity(match.Kind, match.Title, match.ExternalRef, match.FeatureNodeId, match.ParentRequirementId)
            && match.ContentHash == RequirementRules.ContentHash(row.Body);

        return new RequirementPlannedRow(row, same ? ImportOutcome.Unchanged : ImportOutcome.Update, match, identity, parentIsNew);
    }
}
