using System.Text.Json;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>
/// Case identity registration (03.1.1-T1.4/T2.1): the catalog sync hands over the tree of one commit and this
/// resolves it against the project's cases, inside the caller's tenant transaction. Set-based and proportional to
/// the cases that <em>change</em>: one read of the discovered ids, one batched insert, one <c>unnest</c> update for
/// moves and returns, one update for the vanished — an unchanged case costs no write, so no spurious version bump.
/// </summary>
internal sealed partial class CaseIdentityService(
    ITenantDbContextFactory<CatalogDbContext> contexts,
    ITenantDb db,
    ITenantScopeAccessor scopes,
    CaseDiscovery discovery) : ICaseIdentityService
{
    private const string ExistingSql = """
        select id, annotation_id, file_path, status
          from public.test_case
         where org_id = @org_id
           and project_id = @project_id
           and annotation_id in (select t.value::uuid from jsonb_array_elements_text(@ids::jsonb) as t(value))
        """;

    private const string MoveSql = """
        update public.test_case c
           set file_path = u.path,
               status = case when c.status = 'missing' then 'active' else c.status end,
               last_seen_commit = @commit
          from jsonb_to_recordset(@moves::jsonb) as u(id uuid, path text)
         where c.id = u.id
        """;

    private const string MissingSql = """
        update public.test_case
           set status = 'missing',
               last_seen_commit = @commit
         where org_id = @org_id
           and project_id = @project_id
           and status = 'active'
           and annotation_id not in (select t.value::uuid from jsonb_array_elements_text(@seen::jsonb) as t(value))
           and (file_path is null
                or file_path not in (select t.value from jsonb_array_elements_text(@held_paths::jsonb) as t(value)))
        """;

    // The tenant Dapper path is read-only and refuses array parameters (see StringArrayTypeHandler), so the id and
    // path lists travel as JSON text and are unpacked in SQL: one parameter, one plan, whatever the tree's size.
    private static string Json<T>(IEnumerable<T> values) => JsonSerializer.Serialize(values, JsonSerializerOptions.Web);

    /// <inheritdoc />
    public async ValueTask<CaseSyncOutcome> RegisterDiscoveredAsync(
        Guid projectId,
        string commitSha,
        IReadOnlyList<SourceFile> files,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(commitSha);

        if (!CommitPattern().IsMatch(commitSha))
        {
            throw new ArgumentException("the commit must be 7-64 lower-case hex characters", nameof(commitSha));
        }

        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(RegisterDiscoveredAsync));

        var orgId = scopes.Require().OrgId;

        var exists = await db.ScalarAsync<int?>(CatalogReadSql.ProjectExists, new { project_id = projectId }, cancellationToken)
            .ConfigureAwait(false);
        if (exists is null)
        {
            return new CaseSyncOutcome(null, "project_not_found");
        }

        var found = discovery.Discover(files);
        var entries = new List<CaseSyncEntry>(found.Problems);

        var existing = found.Cases.Count == 0
            ? []
            : (await db.QueryAsync<ExistingCase>(
                ExistingSql,
                new { org_id = orgId, project_id = projectId, ids = Json(found.Cases.Select(item => item.Id)) },
                cancellationToken).ConfigureAwait(false)).ToDictionary(row => row.AnnotationId);

        await using var context = contexts.Create();

        int registered = 0, renamed = 0, unchanged = 0, reactivated = 0;
        var moves = new List<MoveRow>();

        foreach (var item in found.Cases)
        {
            if (!existing.TryGetValue(item.Id, out var row))
            {
                var caseId = Guid.CreateVersion7();
                await context.Cases.AddAsync(new TestCase
                {
                    Id = caseId,
                    OrgId = orgId,
                    ProjectId = projectId,
                    AnnotationId = item.Id,
                    Name = item.Name,
                    Framework = item.Framework,
                    Language = item.Language,
                    FilePath = item.Path,
                    FirstSeenCommit = commitSha,
                }, cancellationToken).ConfigureAwait(false);
                await context.CaseIdentitySources.AddAsync(new CaseIdentitySource
                {
                    Id = Guid.CreateVersion7(),
                    OrgId = orgId,
                    CaseId = caseId,
                    Source = item.Source,
                    RawKey = item.RawKey,
                    CommitSha = commitSha,
                }, cancellationToken).ConfigureAwait(false);
                entries.Add(new CaseSyncEntry(item.Path, CaseSyncResults.Registered, caseId));
                registered++;
                continue;
            }

            var moved = !string.Equals(row.FilePath, item.Path, StringComparison.Ordinal);
            var returning = row.Status == CaseStatus.Missing;

            if (!moved && !returning)
            {
                unchanged++;
                continue;
            }

            moves.Add(new MoveRow(row.Id, item.Path));

            if (returning)
            {
                reactivated++;
            }

            if (moved)
            {
                if (row.FilePath is not null)
                {
                    await context.CaseRenames.AddAsync(new CaseRenameHistory
                    {
                        Id = Guid.CreateVersion7(),
                        OrgId = orgId,
                        CaseId = row.Id,
                        FromPath = row.FilePath,
                        ToPath = item.Path,
                        CommitSha = commitSha,
                    }, cancellationToken).ConfigureAwait(false);
                }

                renamed++;
                entries.Add(new CaseSyncEntry(item.Path, CaseSyncResults.Renamed, row.Id));
            }
        }

        if (moves.Count > 0)
        {
            await db.ExecuteAsync(
                MoveSql,
                new { commit = commitSha, moves = Json(moves) },
                cancellationToken).ConfigureAwait(false);
        }

        if (context.ChangeTracker.HasChanges())
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // Vanished: active cases whose id is neither in the tree nor held by a conflict, and whose file is not one
        // a conflict or warning is about (a file with a broken annotation still exists).
        var seen = Json(found.Cases.Select(item => item.Id).Concat(found.HeldIds));
        var heldPaths = Json(found.Problems.Select(problem => problem.Path).Distinct(StringComparer.Ordinal));
        var missing = await db.ExecuteAsync(
            MissingSql,
            new { commit = commitSha, org_id = orgId, project_id = projectId, seen, held_paths = heldPaths },
            cancellationToken).ConfigureAwait(false);

        var conflicts = found.Problems.Count(problem => problem.Result != CaseSyncResults.Warning);
        var warnings = found.Problems.Count - conflicts;

        return new CaseSyncOutcome(
            new CaseSyncReport(
                [.. entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ThenBy(entry => entry.Result, StringComparer.Ordinal)],
                registered,
                renamed,
                unchanged,
                conflicts,
                warnings,
                missing,
                reactivated),
            null);
    }

    [GeneratedRegex("^[0-9a-f]{7,64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CommitPattern();

    private sealed record MoveRow(Guid Id, string Path);

    private sealed class ExistingCase
    {
        public Guid Id { get; set; }

        public Guid AnnotationId { get; set; }

        public string? FilePath { get; set; }

        public string Status { get; set; } = string.Empty;
    }
}
