// The `raw_report` table as PostgreSQL enforces it: the two composite tenant keys, the parser release reference,
// the outcome/refusal CHECKs, both idempotency keys, the append-only grants and trigger, and the tenant boundary
// (ENABLE + FORCE row level security). Each accepted write commits on its own; a statement expected to be refused
// gets its own transaction, because a failed statement aborts the one it runs in.
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// A parse record is written once per report artifact and parser release, is never rewritten, and is readable
/// only inside its own org — so the database has to refuse a row naming another org's run or artifact, a refusal
/// that carries counters or discrepancies, a second row under the same release key, and every update or delete.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RawReportSchemaTests
{
    private const string Table = "raw_report";
    private const string DefaultParserId = "junit-pytest";
    private const string DefaultParserVersion = "1.0.0";
    private const string DefaultProducerKind = "pytest-junitxml";

    private static readonly string[] OutcomeRefusals =
        ["raw_report_outcome_check", "raw_report_partial_check", "raw_report_reason_check"];

    private static readonly string[] ParserVersions = ["1.0.0", "1.1.0", "1.2.0"];

    private readonly PostgresTenancyFixture _fixture;

    public RawReportSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RawReport_Rls_HidesOtherOrgsAndRefusesAForeignTenantWrite()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var own = await SeedTenantAsync(database);
        var other = await SeedTenantAsync(database);
        var owner = _fixture.OwnerConnectionString(database);
        var ownRow = Baseline(own);
        var otherRow = Baseline(other);

        (await InsertAsync(owner, null, ownRow)).ShouldBe(1);
        (await InsertAsync(owner, null, otherRow)).ShouldBe(1);

        var rowSecurity = await _fixture.RowSecurityAsync(database, Table);
        rowSecurity.Enabled.ShouldBeTrue($"{Table} carries ENABLE row level security");
        rowSecurity.Forced.ShouldBeTrue(
            $"{Table} carries FORCE row level security, so the owning role cannot bypass the tenant boundary");

        (await _fixture.ScalarAsync<bool>(
            database, "select rolsuper or rolbypassrls from pg_catalog.pg_roles where rolname = 'evidgate_app'"))
            .ShouldBeFalse("the application role must reach the table through the policy, never through BYPASSRLS");
        (await _fixture.ScalarAsync<string>(
            database, $"select tableowner from pg_catalog.pg_tables where schemaname = 'public' and tablename = '{Table}'"))
            .ShouldBe("evidgate_owner", "the application role is not the table owner");

        var app = _fixture.AppConnectionString(database);
        (await VisibleIdsAsync(app, null)).ShouldBeEmpty("no tenant context returns no rows");
        (await VisibleIdsAsync(app, Guid.Empty)).ShouldBeEmpty("a tenant no row is filed under sees none");
        (await VisibleIdsAsync(app, own.Org)).ShouldBe(new[] { ownRow.Id });
        (await VisibleIdsAsync(app, other.Org)).ShouldBe(new[] { otherRow.Id });

        var outsideError = await RefusedAsync(
            app,
            own.Org,
            Baseline(own) with { Org = other.Org, Run = other.Run, Artifact = other.Artifact });
        outsideError.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        outsideError.MessageText.ShouldContain("row-level security");

        // Positive control: the same row shape filed under the caller's own org is accepted.
        var appended = await SeedArtifactAsync(database, own.Org, own.Project, own.Run);
        (await InsertAsync(app, own.Org, Baseline(own) with { Artifact = appended })).ShouldBe(1);
    }

    [Fact]
    public async Task AppRole_HoldsSelectAndInsert_ButNoUpdateDeleteOrTruncate()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var tenant = await SeedTenantAsync(database);
        (await InsertAsync(_fixture.OwnerConnectionString(database), null, Baseline(tenant))).ShouldBe(1);

        foreach (var (privilege, granted) in new[] { ("SELECT", true), ("INSERT", true), ("UPDATE", false), ("DELETE", false), ("TRUNCATE", false) })
        {
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, privilege))
                .ShouldBe(granted, $"raw_report is append-only for the application role ({privilege})");
        }

        // The grant facts above are pinned against a row the same role can read.
        (await VisibleIdsAsync(_fixture.AppConnectionString(database), tenant.Org)).Length.ShouldBe(1);
    }

    [Fact]
    public async Task AppRole_UpdateDeleteAndTruncate_AreRefusedByGrantAndTheRowSurvives()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var tenant = await SeedTenantAsync(database);
        var row = Baseline(tenant);
        var app = _fixture.AppConnectionString(database);

        (await InsertAsync(app, tenant.Org, row)).ShouldBe(1);

        foreach (var statement in MutationStatements(row.Id))
        {
            var error = await RefusedAsync(app, tenant.Org, Literal(statement));
            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, statement);
            error.MessageText.ShouldContain("permission denied", customMessage: statement);
        }

        (await RowsAsync(database)).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(
            database, $"select outcome from public.raw_report where id = '{row.Id}'"))
            .ShouldBe("mapped");
    }

    [Fact]
    public async Task OwnerRole_UpdateAndDelete_RaiseRawReportImmutable()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var tenant = await SeedTenantAsync(database);
        var row = Baseline(tenant);
        var owner = _fixture.OwnerConnectionString(database);

        (await InsertAsync(owner, null, row)).ShouldBe(1);

        foreach (var statement in MutationStatements(row.Id).Where(sql => !sql.StartsWith("truncate", StringComparison.Ordinal)))
        {
            var error = await RefusedAsync(owner, null, Literal(statement));
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, statement);
            error.MessageText.ShouldContain("raw_report_immutable", customMessage: statement);
        }

        // Positive control: the owner may append a second row for another artifact of the same tenant.
        var secondArtifact = await SeedArtifactAsync(database, tenant.Org, tenant.Project, tenant.Run);
        (await InsertAsync(owner, null, Baseline(tenant) with { Artifact = secondArtifact })).ShouldBe(1);

        (await RowsAsync(database)).ShouldBe(2);
        (await _fixture.ScalarAsync<string>(
            database, $"select outcome from public.raw_report where id = '{row.Id}'"))
            .ShouldBe("mapped");
    }

    [Fact]
    public async Task RawReport_AnotherOrgsRunOrArtifact_IsRefusedByTheCompositeKey()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var own = await SeedTenantAsync(database);
        var other = await SeedTenantAsync(database);
        var owner = _fixture.OwnerConnectionString(database);

        (string Constraint, ReportRow Row)[] refused =
        [
            ("raw_report_run_fk", Baseline(own) with { Org = other.Org, Run = own.Run, Artifact = other.Artifact }),
            ("raw_report_run_fk", Baseline(own) with { Run = other.Run }),
            ("raw_report_run_fk", Baseline(own) with { Run = Guid.CreateVersion7() }),
            ("raw_report_artifact_fk", Baseline(own) with { Org = other.Org, Run = other.Run, Artifact = own.Artifact }),
            ("raw_report_artifact_fk", Baseline(own) with { Artifact = Guid.CreateVersion7() }),
        ];

        foreach (var (constraint, refusedRow) in refused)
        {
            var error = await RefusedAsync(owner, null, refusedRow);
            error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation, constraint);
            error.ConstraintName.ShouldBe(constraint);
        }

        // Positive control: the same row shape, corrected to this org's own run and artifact.
        (await InsertAsync(owner, null, Baseline(own))).ShouldBe(1);
        (await RowsAsync(database)).ShouldBe(1);
    }

    [Fact]
    public async Task RawReport_OutcomeAndParserPair_FollowTheirChecks()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var tenant = await SeedTenantAsync(database);
        var owner = _fixture.OwnerConnectionString(database);

        (string Constraint, ReportRow Row)[] refused =
        [
            // A parsed report names the release that read it: `mapped` without a pair cannot answer "who judged it".
            ("raw_report_parser_pair_check", Baseline(tenant) with { ParserId = null, ParserVersion = null }),
            ("raw_report_parser_pair_check", Baseline(tenant) with { ParserVersion = null }),
            // `parsed` is outside the closed outcome list; more than one arm rejects it (the list, the
            // mapped/partial pairing and the reason rule), so the refusal may name any of them.
            ("raw_report_outcome_check", Baseline(tenant) with { Outcome = "parsed" }),
            // A parsed report carries no refusal reason; a refused one carries a code word.
            ("raw_report_reason_check", Baseline(tenant) with { Reason = "malformed" }),
            ("raw_report_reason_check", Baseline(tenant) with { Outcome = "malformed" }),
            ("raw_report_reason_check", Baseline(tenant) with { Outcome = "malformed", Reason = "Malformed" }),
        ];

        foreach (var (constraint, refusedRow) in refused)
        {
            var error = await RefusedAsync(owner, null, refusedRow);
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, constraint);
            if (constraint == "raw_report_outcome_check")
            {
                // PostgreSQL reports whichever CHECK it evaluates first; an unknown outcome breaks the closed
                // list, the mapped/partial arms and the reason pairing at once.
                OutcomeRefusals.ShouldContain(error.ConstraintName);
            }
            else
            {
                error.ConstraintName.ShouldBe(constraint);
            }
        }

        // The pair is a real release in the registry, not free text: an unknown one is the foreign key's answer.
        var unknownRelease = await RefusedAsync(
            owner, null, Baseline(tenant) with { ParserId = "no-such-parser" });
        unknownRelease.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        unknownRelease.ConstraintName.ShouldBe("raw_report_parser_fk");

        // The refusal shape is accepted: a malformed report, and an unsupported one with no parser resolved (the
        // pair is left null) as well as one whose release exists but could not be loaded.
        (await InsertAsync(owner, null, Baseline(tenant) with
        {
            Artifact = await NewArtifactAsync(database, tenant),
            Outcome = "malformed",
            Reason = "malformed",
        })).ShouldBe(1);
        (await InsertAsync(owner, null, NoParser(tenant) with { Artifact = await NewArtifactAsync(database, tenant) })).ShouldBe(1);
        (await InsertAsync(owner, null, Refused(tenant, "unsupported", "parser_unavailable") with
        {
            Artifact = await NewArtifactAsync(database, tenant),
        })).ShouldBe(1);
        (await RowsAsync(database)).ShouldBe(3);
    }

    [Fact]
    public async Task RawReport_RefusedRowsCarryNoCountersOrDiscrepancies()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var tenant = await SeedTenantAsync(database);
        var owner = _fixture.OwnerConnectionString(database);
        var malformed = Refused(tenant, "malformed", "malformed");

        (string Constraint, ReportRow Row)[] refused =
        [
            // A refused report produced no events, so an observed count or a declared count is a contradiction.
            ("raw_report_refused_empty_check", malformed with { ObservedTests = 1 }),
            ("raw_report_refused_empty_check", malformed with { DeclaredTests = 1 }),
            // Nothing to compare means no discrepancy list either.
            ("raw_report_refused_empty_check",
                NoParser(tenant) with
                {
                    Discrepancies = """[{"code":"unmapped_case","count":1}]""",
                }),
        ];

        foreach (var (constraint, refusedRow) in refused)
        {
            var error = await RefusedAsync(owner, null, refusedRow);
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, constraint);
            error.ConstraintName.ShouldBe(constraint);
        }

        // The empty refusal is the shape the writer stores, so it is accepted.
        (await InsertAsync(owner, null, malformed)).ShouldBe(1);
        (await RowsAsync(database)).ShouldBe(1);
    }

    [Fact]
    public async Task RawReport_PartialAndDiscrepancyShape_FollowTheirChecks()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var tenant = await SeedTenantAsync(database);
        var owner = _fixture.OwnerConnectionString(database);
        var one = """[{"code":"count_mismatch","count":1}]""";

        (string Constraint, ReportRow Row)[] refused =
        [
            // `mapped` means the declared and observed views agree; `partial` means at least one discrepancy.
            ("raw_report_partial_check", Baseline(tenant) with { Discrepancies = one }),
            ("raw_report_partial_check", Baseline(tenant) with { Outcome = "partial" }),
            // The list is a bounded array of code/count objects, never a free-form document.
            ("raw_report_discrepancy_shape_check", Baseline(tenant) with { Outcome = "partial", Discrepancies = """{"code":"count_mismatch"}""" }),
            ("raw_report_discrepancy_shape_check",
                Baseline(tenant) with { Outcome = "partial", Discrepancies = Discrepancies(17) }),
            // Outcomes, failures and skips are counted within the tests that produced them.
            ("raw_report_observed_check", Baseline(tenant) with { ObservedTests = 0, ObservedFailures = 1 }),
            ("raw_report_observed_check", Baseline(tenant) with { ObservedTests = -1 }),
            ("raw_report_declared_check", Baseline(tenant) with { DeclaredTests = -1 }),
            ("raw_report_producer_kind_check", Baseline(tenant) with { ProducerKind = "Pytest" }),
        ];

        foreach (var (constraint, refusedRow) in refused)
        {
            var error = await RefusedAsync(owner, null, refusedRow);
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, constraint);
            error.ConstraintName.ShouldBe(constraint);
        }

        // The bounds themselves are inside the vocabulary. Each accepted row is a different report artifact:
        // the key is (artifact, parser pair), so reusing one would collide on the unique index.
        (await InsertAsync(owner, null, Baseline(tenant) with
        {
            Artifact = await NewArtifactAsync(database, tenant),
            Outcome = "partial",
            Discrepancies = one,
        })).ShouldBe(1);
        (await InsertAsync(owner, null, Baseline(tenant) with
        {
            Artifact = await NewArtifactAsync(database, tenant),
            Outcome = "partial",
            Discrepancies = Discrepancies(16),
        })).ShouldBe(1);
        (await InsertAsync(owner, null, Baseline(tenant) with
        {
            Artifact = await NewArtifactAsync(database, tenant),
            DeclaredTests = 0,
            ObservedTests = 0,
        })).ShouldBe(1);
        (await RowsAsync(database)).ShouldBe(3);
    }

    [Fact]
    public async Task RawReport_SecondParseOfTheSameRelease_IsRefusedAndOnConflictWritesNothing()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var tenant = await SeedTenantAsync(database);
        var owner = _fixture.OwnerConnectionString(database);
        var app = _fixture.AppConnectionString(database);

        (await InsertAsync(app, tenant.Org, Baseline(tenant))).ShouldBe(1);

        var duplicate = await RefusedAsync(app, tenant.Org, Baseline(tenant));
        duplicate.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        duplicate.ConstraintName.ShouldBe("raw_report_artifact_parser_key");

        // The handler's replay insert: the same key, `on conflict do nothing`, writes no second row.
        (await InsertAsync(app, tenant.Org, Baseline(tenant), onConflictDoNothing: true)).ShouldBe(0);
        (await RowsAsync(database)).ShouldBe(1);

        // A newer release is a new row beside the old one, not an edit of it.
        await SeedParserVersionAsync(database, "1.1.0");
        (await InsertAsync(app, tenant.Org, Baseline(tenant) with { ParserVersion = "1.1.0" })).ShouldBe(1);

        // A report no parser resolved for keys on the producer kind it declared, leaving the pair null.
        (await InsertAsync(app, tenant.Org, NoParser(tenant))).ShouldBe(1);
        var kindDuplicate = await RefusedAsync(app, tenant.Org, NoParser(tenant));
        kindDuplicate.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        kindDuplicate.ConstraintName.ShouldBe("raw_report_artifact_kind_key");
        (await InsertAsync(app, tenant.Org, NoParser(tenant), onConflictDoNothing: true)).ShouldBe(0);

        // The kind is part of that key: another producer's declaration is another row.
        (await InsertAsync(app, tenant.Org, NoParser(tenant) with { ProducerKind = "playwright-json" })).ShouldBe(1);

        (await RowsAsync(database)).ShouldBe(4);
    }

    [Fact]
    public async Task RawReport_RunComparisonRead_UsesTheRunIndex()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var tenant = await SeedTenantAsync(database);
        var app = _fixture.AppConnectionString(database);

        await SeedParserVersionAsync(database, "1.1.0");
        await SeedParserVersionAsync(database, "1.2.0");
        var rows = ParserVersions
            .Select(version => Baseline(tenant) with { ParserVersion = version })
            .ToArray();
        foreach (var row in rows)
        {
            (await InsertAsync(app, tenant.Org, row)).ShouldBe(1);
        }

        // A second run of the same org, carrying many more reports: without it the table is three rows wide
        // and an org_id index is as cheap as the run index, so the plan would prove nothing about selectivity.
        var otherRun = await RunRowSeeder.SeedRunAsync(_fixture, database, tenant.Org);
        for (var index = 0; index < 20; index++)
        {
            var artifact = await SeedArtifactAsync(database, tenant.Org, tenant.Project, otherRun);
            (await InsertAsync(app, tenant.Org, Baseline(tenant) with { Run = otherRun, Artifact = artifact }))
                .ShouldBe(1);
        }

        await _fixture.ExecuteAsync(database, "analyze public.raw_report");

        await using var scope = await TenantScope.BeginAsync(app, tenant.Org);
        await scope.SetAsync("enable_seqscan", "off");

        // The plan is read with the predicate's values inlined: an EXPLAIN of a parameterised statement plans
        // a generic statement, which is free to answer without a scan at all. The values are generated
        // UUIDs, never input, and the read below still runs parameterised.
        var plan = await scope.ScalarAsync<string>(
            "explain (format json) select id from public.raw_report "
            + $"where org_id = '{tenant.Org:D}'::uuid and run_id = '{tenant.Run:D}'::uuid");
        plan.ShouldContain("raw_report_run_idx");

        var ids = await scope.ScalarAsync<Guid[]>(
            "select coalesce(array_agg(id order by id), '{}'::uuid[]) from public.raw_report "
            + "where org_id = @org and run_id = @run",
            ("org", tenant.Org),
            ("run", tenant.Run));
        ids.ShouldBe(rows.Select(row => row.Id).ToArray(), ignoreOrder: true);
    }

    /// <summary>An organisation with one project, one committed run and one <c>ready</c> raw-report artifact.</summary>
    private sealed record Tenant(Guid Org, Guid Project, Guid Run, Guid Artifact);

    /// <summary>One <c>raw_report</c> row's members; the defaults are the accepted mapped baseline.</summary>
    private sealed record ReportRow
    {
        internal required Guid Org { get; init; }

        internal required Guid Run { get; init; }

        internal required Guid Artifact { get; init; }

        internal Guid Id { get; init; } = Guid.CreateVersion7();

        internal string ProducerKind { get; init; } = DefaultProducerKind;

        internal string? ParserId { get; init; } = DefaultParserId;

        internal string? ParserVersion { get; init; } = DefaultParserVersion;

        internal string Outcome { get; init; } = "mapped";

        internal string? Reason { get; init; }

        internal int? DeclaredTests { get; init; }

        internal int ObservedTests { get; init; }

        internal int ObservedFailures { get; init; }

        internal string Discrepancies { get; init; } = "[]";
    }

    private static ReportRow Baseline(Tenant tenant) => new() { Org = tenant.Org, Run = tenant.Run, Artifact = tenant.Artifact };

    /// <summary>The row of a refused report: no counters, no discrepancies, a code word and the given release.</summary>
    private static ReportRow Refused(Tenant tenant, string outcome, string reason) =>
        Baseline(tenant) with { Outcome = outcome, Reason = reason };

    /// <summary>
    /// The row of a report no parser resolved for: `unsupported` with the parser pair left null, which is the
    /// only shape whose pair may be absent (and the only one whose key is the producer kind).
    /// </summary>
    private static ReportRow NoParser(Tenant tenant) =>
        Baseline(tenant) with
        {
            Outcome = "unsupported",
            Reason = "no_parser",
            ParserId = null,
            ParserVersion = null,
        };

    private static string Discrepancies(int count) =>
        "[" + string.Join(",", Enumerable.Repeat("""{"code":"count_mismatch","count":1}""", count)) + "]";

    private static IEnumerable<string> MutationStatements(Guid id) =>
    [
        $"update public.raw_report set outcome = 'partial' where id = '{id}'",
        $"delete from public.raw_report where id = '{id}'",
        "truncate public.raw_report",
    ];

    private static (string Sql, (string Name, object Value)[] Parameters) Literal(string sql) =>
        (sql, Array.Empty<(string Name, object Value)>());

    private static (string Sql, (string Name, object Value)[] Parameters) Insert(ReportRow row, bool onConflictDoNothing) =>
        ("insert into public.raw_report "
            + "(id, org_id, run_id, artifact_id, producer_kind, parser_id, parser_version, outcome, reason, "
            + "declared_tests, observed_tests, observed_failures, discrepancies) "
            + "values (@id, @org, @run, @artifact, @producer, @parser_id::text, @parser_version::text, @outcome, "
            + "@reason::text, @declared::integer, @observed::integer, @failures::integer, @discrepancies::jsonb)"
            + (onConflictDoNothing ? " on conflict do nothing" : string.Empty),
            new (string Name, object Value)[]
            {
                ("id", row.Id),
                ("org", row.Org),
                ("run", row.Run),
                ("artifact", row.Artifact),
                ("producer", row.ProducerKind),
                ("parser_id", NullOr(row.ParserId)),
                ("parser_version", NullOr(row.ParserVersion)),
                ("outcome", row.Outcome),
                ("reason", NullOr(row.Reason)),
                ("declared", NullOr(row.DeclaredTests)),
                ("observed", row.ObservedTests),
                ("failures", row.ObservedFailures),
                ("discrepancies", row.Discrepancies),
            });

    private static object NullOr(string? value) => value ?? (object)DBNull.Value;

    private static object NullOr(int? value) => value ?? (object)DBNull.Value;

    /// <summary>One organisation and project, its run, and one artifact of that run.</summary>
    private async Task<Tenant> SeedTenantAsync(string database)
    {
        var (org, project) = await FixtureSeed.TenantAsync(_fixture, database);
        var run = await RunRowSeeder.SeedRunAsync(_fixture, database, org);

        return new Tenant(org, project, run, await SeedArtifactAsync(database, org, project, run));
    }

    /// <summary>One more artifact of the tenant's run, so the row under test has an artifact key of its own.</summary>
    private async Task<Guid> NewArtifactAsync(string database, Tenant tenant) =>
        await SeedArtifactAsync(database, tenant.Org, tenant.Project, tenant.Run);

    /// <summary>One committed <c>ready</c> raw-report artifact for a run, written through the application role.</summary>
    private async Task<Guid> SeedArtifactAsync(string database, Guid org, Guid project, Guid run)
    {
        var id = Guid.CreateVersion7();
        var executionKey = new string('a', 64);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
        (await scope.ExecuteAsync(
            """
            insert into public.artifact
                (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum,
                 sensitivity, state)
            values (@id, @org, @project, @run, @execution_key, 1, 'raw_report', @object_key, 'application/xml',
                    @checksum, 'normal', 'ready')
            """,
            ("id", id),
            ("org", org),
            ("project", project),
            ("run", run),
            ("execution_key", executionKey),
            ("object_key", $"org/{org:D}/project/{project:D}/run/{run:D}/attempt/{executionKey}/1/{id:D}"),
            ("checksum", "sha256:" + new string('b', 64)))).ShouldBe(1);
        await scope.CommitAsync();

        return id;
    }

    /// <summary>One registry release beside the built-in row the migration seeds, written as the owner.</summary>
    private async Task SeedParserVersionAsync(string database, string version)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        (await scope.ExecuteAsync(
            """
            insert into public.report_parser (id, org_id, parser_id, version, producer_kind, schema_version, notes)
            values (@id, null, @parser_id, @version, @producer, 1, 'probe')
            """,
            ("id", Guid.CreateVersion7()),
            ("parser_id", DefaultParserId),
            ("version", version),
            ("producer", DefaultProducerKind))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>Runs one insert in its own committed transaction and returns the rows it wrote.</summary>
    private static async Task<int> InsertAsync(string connectionString, Guid? org, ReportRow row, bool onConflictDoNothing = false)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, org);
        var (sql, parameters) = Insert(row, onConflictDoNothing);
        var affected = await scope.ExecuteAsync(sql, parameters);
        await scope.CommitAsync();

        return affected;
    }

    /// <summary>Runs one statement that must be refused, in a transaction that is rolled back.</summary>
    private static async Task<PostgresException> RefusedAsync(
        string connectionString,
        Guid? org,
        (string Sql, (string Name, object Value)[] Parameters) statement)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, org);

        return await scope.CaptureErrorAsync(statement.Sql, statement.Parameters);
    }

    private static async Task<PostgresException> RefusedAsync(string connectionString, Guid? org, ReportRow row) =>
        await RefusedAsync(connectionString, org, Insert(row, onConflictDoNothing: false));

    /// <summary>The rows visible to the application role with one tenant setting (empty when unset).</summary>
    private static async Task<Guid[]> VisibleIdsAsync(string connectionString, Guid? org)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, org);
        if (org is null)
        {
            await scope.SetAsync("evidgate.org_id", string.Empty);
        }

        return await scope.ScalarAsync<Guid[]>(
            "select coalesce(array_agg(id order by id), '{}'::uuid[]) from public.raw_report");
    }

    private async Task<long> RowsAsync(string database) =>
        await _fixture.ScalarAsync<long>(database, "select count(*) from public.raw_report");
}
