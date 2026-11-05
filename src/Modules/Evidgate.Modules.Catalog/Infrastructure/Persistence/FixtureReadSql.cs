namespace Evidgate.Modules.Catalog.Infrastructure.Persistence;

/// <summary>The Dapper statements of the fixture side, one statement per handler step.</summary>
/// <remarks>
/// A fixture row is immutable and has no head column, so the head is the revision with the greatest number, read
/// through the lateral join that the <c>(org_id, fixture_id, revision_no desc)</c> index serves.
/// </remarks>
internal static class FixtureReadSql
{
    private const string FixtureColumns = """
               f.id, f.org_id, f.project_id, f.fixture_key,
               h.id as head_revision_id, h.revision_no as head_revision_no, h.revision_hash as head_revision_hash,
               f.created_at
        """;

    private const string FixtureHeadJoin = """
          left join lateral (
            select r.id, r.revision_no, r.revision_hash
              from public.fixture_revision r
             where r.org_id = f.org_id
               and r.fixture_id = f.id
             order by r.revision_no desc
             limit 1) h on true
        """;

    private const string RevisionColumns = """
               r.id, r.fixture_id, r.revision_no, r.revision_hash, r.seed_version, r.timeout_s, r.cleanup_policy,
               r.resource_prefix, r.body_ref, r.created_at
        """;

    /// <summary>One fixture with its head's number and hash (null before the first revision).</summary>
    internal const string FixtureById = $"""
        select {FixtureColumns}
          from public.fixture f
        {FixtureHeadJoin}
         where f.id = @fixture_id
        """;

    internal const string FixturesFirstPage = $"""
        select {FixtureColumns}
          from public.fixture f
        {FixtureHeadJoin}
         where f.org_id = @org_id
           and f.project_id = @project_id
         order by f.created_at,
                  f.id
         limit @limit
        """;

    internal const string FixturesNextPage = $"""
        select {FixtureColumns}
          from public.fixture f
        {FixtureHeadJoin}
         where f.org_id = @org_id
           and f.project_id = @project_id
           and (f.created_at, f.id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
         order by f.created_at,
                  f.id
         limit @limit
        """;

    /// <summary>One revision of one fixture (the detail route reads the head revision through it).</summary>
    internal const string RevisionById = $"""
        select {RevisionColumns}
          from public.fixture_revision r
         where r.id = @revision_id
        """;

    /// <summary>Bounds the wait for the revision lock, so a holder that never finishes cannot park the request forever.</summary>
    internal const string LockTimeout = "select set_config('lock_timeout', '5000', true)";

    /// <summary>
    /// Takes the per-fixture transaction lock that serialises <c>revision_no</c> allocation and the hash dedupe, and
    /// holds it until the request's transaction ends.
    /// </summary>
    /// <remarks>
    /// Not <c>select … for update</c>: a row lock needs UPDATE on the table, and the app role holds select and insert
    /// only. The key carries its purpose so it cannot collide with another advisory lock taken on the same id.
    /// </remarks>
    internal const string AcquireFixtureLock =
        "select pg_advisory_xact_lock(hashtextextended('fixture_revision:' || @fixture_id::text, 0))";

    /// <summary>The fixture row a revision write reads under the advisory lock: its identity never changes, so a plain read is enough.</summary>
    internal const string FixtureForRead = """
        select id, project_id, fixture_key
          from public.fixture
         where id = @fixture_id
        """;

    internal const string ExistingByHash = """
        select id, revision_no
          from public.fixture_revision
         where org_id = @org_id
           and fixture_id = @fixture_id
           and revision_hash = @revision_hash
        """;

    internal const string NextRevisionNo = """
        select coalesce(max(revision_no), 0) + 1
          from public.fixture_revision
         where org_id = @org_id
           and fixture_id = @fixture_id
        """;

    internal const string InsertFixture = """
        insert into public.fixture (id, org_id, project_id, fixture_key, created_by)
        values (@id, @org_id, @project_id, @fixture_key, @created_by)
        """;

    internal const string InsertRevision = """
        insert into public.fixture_revision
               (id, org_id, fixture_id, project_id, fixture_key, revision_no, revision_hash, seed_version,
                timeout_s, cleanup_policy, resource_prefix, body_ref, created_by)
        values (@id, @org_id, @fixture_id, @project_id, @fixture_key, @revision_no, @revision_hash, @seed_version,
                @timeout_s, @cleanup_policy, @resource_prefix, @body_ref, @created_by)
        """;

    /// <summary>
    /// The references of a suite revision that this project's fixtures hold at exactly the named revision, as one
    /// statement however many references the request carries. The join through <c>fixture</c> is what makes the answer
    /// project-scoped: row level security already hides another org's rows, and this predicate hides another project's.
    /// </summary>
    internal const string ExistingReferences = """
        select t.name as name, t.revision as revision
          from jsonb_to_recordset(@rows::jsonb) as t(name text, revision integer)
          join public.fixture f on f.project_id = @project_id and f.fixture_key = t.name
          join public.fixture_revision r on r.org_id = f.org_id and r.fixture_id = f.id and r.revision_no = t.revision
        """;
}
