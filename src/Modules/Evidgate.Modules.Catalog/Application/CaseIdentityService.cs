using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>
/// Case identity registration (03.1.1-T1.4/T2.1) and revision append (03.1.3-T1.3): the catalog sync hands over the
/// tree of one commit and this resolves it against the project's cases, inside the caller's tenant transaction.
/// Entirely set-based — the whole tree travels as a few JSON parameters and every step is one statement, so the cost
/// is a handful of round trips whatever the tree's size, and an unchanged case costs no write (so no spurious version
/// bump for a concurrent PATCH). Syncs of one project queue behind a transaction-scoped advisory lock, which removes
/// the insert race and the lock-order deadlocks of two overlapping syncs.
/// </summary>
internal sealed class CaseIdentityService(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    CaseDiscovery discovery) : ICaseIdentityService
{
    /// <summary>A sweep marking more than this share of the active cases missing (and at least <see cref="MassMissingFloor"/>) is refused.</summary>
    internal const int MassMissingFloor = 10;

    private const string LockTimeoutSql = "select set_config('lock_timeout', '10000', true)";

    private const string LockSql = "select pg_advisory_xact_lock(hashtextextended('catalog_sync:' || @project_id, 0))";

    private const string ExistingSql = """
        select id, annotation_id, file_path, status
          from public.test_case
         where org_id = @org_id
           and project_id = @project_id
           and annotation_id in (select t.value::uuid from jsonb_array_elements_text(@ids::jsonb) as t(value))
        """;

    private const string InsertCasesSql = """
        insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, language, file_path, first_seen_commit, requirement_ref)
        select r.id, @org_id, @project_id, r.annotation_id, r.name, r.framework, r.language, r.file_path, @commit, r.requirement_ref
          from jsonb_to_recordset(@rows::jsonb)
               as r(id uuid, annotation_id uuid, name text, framework text, language text, file_path text, requirement_ref text)
        """;

    private const string InsertSourcesSql = """
        insert into public.case_identity_source (id, org_id, case_id, source, raw_key, commit_sha)
        select r.id, @org_id, r.case_id, r.source, r.raw_key, @commit
          from jsonb_to_recordset(@rows::jsonb) as r(id uuid, case_id uuid, source text, raw_key text)
        """;

    private const string InsertRenamesSql = """
        insert into public.case_rename_history (id, org_id, case_id, from_path, to_path, commit_sha)
        select r.id, @org_id, r.case_id, r.from_path, r.to_path, @commit
          from jsonb_to_recordset(@rows::jsonb) as r(id uuid, case_id uuid, from_path text, to_path text)
        """;

    private const string MoveSql = """
        update public.test_case c
           set file_path = u.path,
               status = case when c.status = 'missing' then 'active' else c.status end,
               last_seen_commit = @commit
          from jsonb_to_recordset(@moves::jsonb) as u(id uuid, path text)
         where c.id = u.id
           and c.org_id = @org_id
           and c.project_id = @project_id
        """;

    private const string SweepPredicate = """
         where org_id = @org_id
           and project_id = @project_id
           and status = 'active'
           and annotation_id not in (select t.value::uuid from jsonb_array_elements_text(@seen::jsonb) as t(value))
           and (file_path is null
                or (file_path not in (select t.value from jsonb_array_elements_text(@held_paths::jsonb) as t(value))
                    and not exists (select 1 from jsonb_array_elements_text(@held_dirs::jsonb) as d(value)
                                     where starts_with(file_path, d.value))))
        """;

    private const string MissingCountSql = $"""
        select count(*) filter (where true) as candidates,
               (select count(*) from public.test_case where org_id = @org_id and project_id = @project_id and status = 'active') as active
          from public.test_case
        {SweepPredicate}
        """;

    private const string MissingSql = $"""
        update public.test_case
           set status = 'missing',
               last_seen_commit = @commit
        {SweepPredicate}
        """;

    private static readonly JsonSerializerOptions Snake = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <inheritdoc />
    public async ValueTask<CaseSyncOutcome> RegisterDiscoveredAsync(
        Guid projectId,
        string commitSha,
        IReadOnlyList<SourceFile> files,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(commitSha);

        if (!AsciiShape.IsMatch(commitSha, 7, 64, AsciiShape.LowerHex, AsciiShape.LowerHex))
        {
            throw new ArgumentException("the commit must be 7-64 lower-case hex characters", nameof(commitSha));
        }

        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(RegisterDiscoveredAsync));

        var orgId = scopes.Require().OrgId;

        // The pure CPU work — parsing, hashing, assertion extraction — runs before the transaction takes the project
        // lock, in parallel, so the lock is held for statements only.
        var found = discovery.Discover(files);
        var described = found.Cases.AsParallel().AsOrdered().Select(item => RevisionHasher.Describe(item.Framework, item.Language, item.Content)).ToArray();

        var exists = await db.ScalarAsync<int?>(CatalogReadSql.ProjectExists, new { project_id = projectId }, cancellationToken)
            .ConfigureAwait(false);
        if (exists is null)
        {
            return new CaseSyncOutcome(null, "project_not_found");
        }

        // One sync of a project at a time; the lock is released with the caller's transaction. A sync that waits longer
        // than the timeout fails as busy instead of holding a pooled connection for ever.
        try
        {
            await db.ExecuteAsync(LockTimeoutSql, null, cancellationToken).ConfigureAwait(false);
            await db.ExecuteAsync(LockSql, new { project_id = projectId.ToString("D") }, cancellationToken).ConfigureAwait(false);
        }
        catch (Npgsql.PostgresException exception) when (exception.SqlState == Npgsql.PostgresErrorCodes.LockNotAvailable)
        {
            return new CaseSyncOutcome(null, "sync_busy");
        }

        var entries = new List<CaseSyncEntry>(found.Problems);

        var existing = found.Cases.Count == 0
            ? []
            : (await db.QueryAsync<ExistingCase>(
                ExistingSql,
                new { org_id = orgId, project_id = projectId, ids = Json(found.Cases.Select(item => item.Id)) },
                cancellationToken).ConfigureAwait(false)).ToDictionary(row => row.AnnotationId);

        int registered = 0, renamed = 0, unchanged = 0, reactivated = 0;
        var newCases = new List<NewCase>();
        var sources = new List<NewSource>();
        var renames = new List<NewRename>();
        var moves = new List<MoveRow>();
        var revisions = new List<RevisionRow>();

        for (var index = 0; index < found.Cases.Count; index++)
        {
            var item = found.Cases[index];
            Guid caseId;
            if (!existing.TryGetValue(item.Id, out var row))
            {
                caseId = Guid.CreateVersion7();
                newCases.Add(new NewCase(caseId, item.Id, item.Name, item.Framework, item.Language, item.Path, item.RequirementRef));
                sources.Add(new NewSource(Guid.CreateVersion7(), caseId, item.Source, item.RawKey));
                entries.Add(new CaseSyncEntry(item.Path, CaseSyncResults.Registered, caseId));
                registered++;
            }
            else
            {
                caseId = row.Id;
                var moved = !string.Equals(row.FilePath, item.Path, StringComparison.Ordinal);
                var returning = row.Status == CaseStatus.Missing;

                if (!moved && !returning)
                {
                    unchanged++;
                }
                else
                {
                    moves.Add(new MoveRow(row.Id, item.Path));

                    if (returning)
                    {
                        reactivated++;
                    }

                    if (moved)
                    {
                        if (row.FilePath is not null)
                        {
                            renames.Add(new NewRename(Guid.CreateVersion7(), row.Id, row.FilePath, item.Path));
                        }

                        renamed++;
                        entries.Add(new CaseSyncEntry(item.Path, CaseSyncResults.Renamed, row.Id));
                    }
                    else
                    {
                        entries.Add(new CaseSyncEntry(item.Path, CaseSyncResults.Reactivated, row.Id));
                    }
                }
            }

            var descriptor = described[index];
            revisions.Add(new RevisionRow(
                Guid.CreateVersion7(),
                caseId,
                descriptor.RevisionHash,
                item.Path,
                item.Framework,
                item.Language,
                descriptor.ContentSha256,
                descriptor.AssertionHash,
                JsonSerializer.Deserialize<JsonElement>(descriptor.MetadataJson)));
        }

        // The sweep is decided before anything is written: a tree that looks truncated is refused whole.
        var seen = Json(found.Cases.Select(item => item.Id).Concat(found.HeldIds));
        var sweep = new
        {
            org_id = orgId,
            project_id = projectId,
            seen,
            held_paths = Json(found.Problems.Select(problem => problem.Path).Distinct(StringComparer.Ordinal)),
            held_dirs = Json(found.HeldDirectories),
        };
        var counts = await db.QuerySingleOrDefaultAsync<SweepCounts>(MissingCountSql, sweep, cancellationToken).ConfigureAwait(false)
            ?? new SweepCounts();

        var nothingFound = found.Cases.Count == 0 && found.HeldIds.Count == 0 && found.Problems.Count == 0;
        if (counts.Active > 0 && nothingFound)
        {
            return new CaseSyncOutcome(null, "empty_tree");
        }

        if (counts.Candidates >= MassMissingFloor && counts.Candidates * 2 > counts.Active)
        {
            return new CaseSyncOutcome(null, "incomplete_tree");
        }

        if (newCases.Count > 0)
        {
            await db.ExecuteAsync(
                InsertCasesSql,
                new { org_id = orgId, project_id = projectId, commit = commitSha, rows = Json(newCases) },
                cancellationToken).ConfigureAwait(false);
            await db.ExecuteAsync(
                InsertSourcesSql,
                new { org_id = orgId, commit = commitSha, rows = Json(sources) },
                cancellationToken).ConfigureAwait(false);
        }

        if (renames.Count > 0)
        {
            await db.ExecuteAsync(InsertRenamesSql, new { org_id = orgId, commit = commitSha, rows = Json(renames) }, cancellationToken)
                .ConfigureAwait(false);
        }

        if (moves.Count > 0)
        {
            await db.ExecuteAsync(
                MoveSql,
                new { org_id = orgId, project_id = projectId, commit = commitSha, moves = Json(moves) },
                cancellationToken).ConfigureAwait(false);
        }

        var appended = revisions.Count == 0
            ? 0
            : await db.ExecuteAsync(
                TestRevisionSql.AppendMany,
                new { org_id = orgId, commit_sha = commitSha, revisions = Json(revisions) },
                cancellationToken).ConfigureAwait(false);

        var missing = counts.Candidates == 0
            ? 0
            : await db.ExecuteAsync(MissingSql, new { sweep.org_id, sweep.project_id, commit = commitSha, sweep.seen, sweep.held_paths, sweep.held_dirs }, cancellationToken)
                .ConfigureAwait(false);

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
                reactivated,
                appended),
            null);
    }

    // The tenant Dapper path is read-only and refuses array parameters (see StringArrayTypeHandler), so lists travel
    // as JSON text and are unpacked in SQL: one parameter, one plan, whatever the tree's size.
    private static string Json<T>(IEnumerable<T> values) => JsonSerializer.Serialize(values, Snake);

    private sealed record NewCase(Guid Id, Guid AnnotationId, string Name, string Framework, string? Language, string FilePath, string? RequirementRef);

    private sealed record NewSource(Guid Id, Guid CaseId, string Source, string RawKey);

    private sealed record NewRename(Guid Id, Guid CaseId, string FromPath, string ToPath);

    private sealed record MoveRow(Guid Id, string Path);

    private sealed record RevisionRow(
        Guid Id,
        Guid CaseId,
        string RevisionHash,
        string FilePath,
        string Framework,
        string? Language,
        string ContentSha256,
        string AssertionHash,
        JsonElement Metadata);

    private sealed class SweepCounts
    {
        public long Candidates { get; set; }

        public long Active { get; set; }
    }

    private sealed class ExistingCase
    {
        public Guid Id { get; set; }

        public Guid AnnotationId { get; set; }

        public string? FilePath { get; set; }

        public string Status { get; set; } = string.Empty;
    }
}
