using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>
/// <see cref="ITestRevisionStore"/> over the tenant transaction (03.1.3-T1.3): one statement per append, an
/// <c>insert … on conflict do nothing</c> whose loser reads the winner's row, so two syncs of the same content agree
/// on one id. The bulk path of the sync uses <see cref="TestRevisionSql.AppendMany"/> instead.
/// </summary>
internal sealed class TestRevisionStore(ITenantDb db, ITenantScopeAccessor scopes) : ITestRevisionStore
{
    /// <inheritdoc />
    public async ValueTask<TestRevisionAppend> AppendAsync(Guid caseId, TestRevisionContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(AppendAsync));

        var orgId = scopes.Require().OrgId;
        var descriptor = RevisionHasher.Describe(content.Framework, content.Language, content.Source);

        var row = await db.QuerySingleOrDefaultAsync<AppendRow>(
            TestRevisionSql.AppendOne,
            new
            {
                id = Guid.CreateVersion7(),
                org_id = orgId,
                case_id = caseId,
                revision_hash = descriptor.RevisionHash,
                commit_sha = content.CommitSha,
                file_path = content.FilePath,
                framework = content.Framework,
                language = content.Language,
                content_sha256 = descriptor.ContentSha256,
                assertion_hash = descriptor.AssertionHash,
                metadata = descriptor.MetadataJson,
            },
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("the revision insert returned neither a new nor an existing row");

        return new TestRevisionAppend(row.Id, descriptor.RevisionHash, row.Created);
    }

    private sealed class AppendRow
    {
        public Guid Id { get; set; }

        public bool Created { get; set; }
    }
}

/// <summary>The statements of the revision store.</summary>
internal static class TestRevisionSql
{
    /// <summary>One append; returns the row id and whether this call created it.</summary>
    internal const string AppendOne = """
        with ins as (
          insert into public.test_revision
                 (id, org_id, case_id, revision_hash, commit_sha, file_path, framework, language, content_sha256, assertion_hash, metadata)
          values (@id, @org_id, @case_id, @revision_hash, @commit_sha, @file_path, @framework, @language, @content_sha256, @assertion_hash, @metadata::jsonb)
          on conflict (org_id, case_id, revision_hash) do nothing
          returning id
        )
        select id, true as created from ins
        union all
        select r.id, false
          from public.test_revision r
         where r.org_id = @org_id
           and r.case_id = @case_id
           and r.revision_hash = @revision_hash
           and not exists (select 1 from ins)
        """;

    /// <summary>
    /// The sync's bulk append: every described file of a tree in one statement; rows that already exist are skipped by
    /// the unique key. Returns the number of revisions created.
    /// </summary>
    internal const string AppendMany = """
        insert into public.test_revision
               (id, org_id, case_id, revision_hash, commit_sha, file_path, framework, language, content_sha256, assertion_hash, metadata)
        select r.id, @org_id, r.case_id, r.revision_hash, @commit_sha, r.file_path, r.framework, r.language, r.content_sha256, r.assertion_hash, r.metadata
          from jsonb_to_recordset(@revisions::jsonb)
               as r(id uuid, case_id uuid, revision_hash text, file_path text, framework text, language text,
                    content_sha256 text, assertion_hash text, metadata jsonb)
        on conflict (org_id, case_id, revision_hash) do nothing
        """;
}
