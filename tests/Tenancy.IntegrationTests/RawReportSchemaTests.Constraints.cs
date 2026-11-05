// The CHECK and reference half of the `raw_report` schema cases: the outcome/parser-pair rules, the refusal shape,
// the bounded discrepancy list and its element shape, the parser release's visibility to the row's org, the two
// idempotency keys, and the run-comparison index. Each accepted write commits on its own; a refused statement gets
// its own transaction, because a failed statement aborts the one it runs in.
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class RawReportSchemaTests
{
    private static readonly string[] OutcomeRefusals =
        ["raw_report_outcome_check", "raw_report_partial_check", "raw_report_reason_check"];

    private static readonly string[] ParserVersions = ["1.0.0", "1.1.0", "1.2.0"];

    [Fact]
    public async Task RawReport_OutcomeAndParserPair_FollowTheirChecks()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var tenant = await SeedTenantAsync(database);
        var owner = _fixture.OwnerConnectionString(database);

        (string Constraint, ReportRow Row)[] refused =
        [
            // A parsed report names the release that read it: `mapped` without a pair cannot answer "who judged it".
            // A pair with no id skips the visibility trigger (its WHEN is `parser_id is not null`) and meets the CHECK.
            ("raw_report_parser_pair_check", Baseline(tenant) with { ParserId = null, ParserVersion = null }),
            ("raw_report_parser_pair_check", Baseline(tenant) with { ParserId = null }),
            // The reverse — an id with no version — names no catalogue row at all, and the BEFORE INSERT trigger
            // runs before the table CHECKs, so the visibility guard is what refuses it.
            ("raw_report_parser_invisible", Baseline(tenant) with { ParserVersion = null }),
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

        // The pair is a release in the registry, not free text: a release the org cannot see is refused by the
        // visibility guard, which answers before the foreign key's own check for every non-null pair.
        var unknownRelease = await RefusedAsync(
            owner, null, Baseline(tenant) with { ParserId = "no-such-parser" });
        unknownRelease.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        unknownRelease.ConstraintName.ShouldBe("raw_report_parser_invisible");

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
        var one = Discrepancies(1);

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
    public async Task RawReport_DiscrepancyElement_IsExactlyACodeAndAPositiveIntegerCount()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var tenant = await SeedTenantAsync(database);
        var owner = _fixture.OwnerConnectionString(database);

        string[] refused =
        [
            // A free-form element instead of an object.
            """["count_mismatch"]""",
            // A message body beside the pair is exactly what the shape must keep out of the row.
            """[{"code":"count_mismatch","count":1,"message":"expected 3, saw 4"}]""",
            // Either member missing, and a key the vocabulary does not name.
            """[{"code":"count_mismatch"}]""",
            """[{"count":1}]""",
            """[{"code":"count_mismatch","count":1,"case_name":"checkout"}]""",
            // The count is a positive integer: not zero, not negative, not fractional, not a string.
            """[{"code":"count_mismatch","count":0}]""",
            """[{"code":"count_mismatch","count":-1}]""",
            """[{"code":"count_mismatch","count":1.5}]""",
            """[{"code":"count_mismatch","count":"1"}]""",
            // The code is the mapper's closed lower_snake vocabulary, not a case name.
            """[{"code":"CountMismatch","count":1}]""",
            // One valid element does not excuse an invalid neighbour.
            """[{"code":"count_mismatch","count":1},{"code":"unmapped_case","count":0}]""",
        ];

        foreach (var discrepancies in refused)
        {
            var error = await RefusedAsync(owner, null, Baseline(tenant) with
            {
                Artifact = await NewArtifactAsync(database, tenant),
                Outcome = "partial",
                Discrepancies = discrepancies,
            });
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, discrepancies);
            error.ConstraintName.ShouldBe("raw_report_discrepancy_shape_check", customMessage: discrepancies);
        }

        // Positive controls: one element, a count above one, and the sixteen-element bound the array rule allows.
        foreach (var discrepancies in new[]
                 {
                     Discrepancies(1),
                     """[{"code":"unmapped_case","count":17}]""",
                     Discrepancies(16),
                 })
        {
            (await InsertAsync(owner, null, Baseline(tenant) with
            {
                Artifact = await NewArtifactAsync(database, tenant),
                Outcome = "partial",
                Discrepancies = discrepancies,
            })).ShouldBe(1, discrepancies);
        }

        (await RowsAsync(database)).ShouldBe(3);
    }

    [Fact]
    public async Task RawReport_ParserRelease_IsVisibleToItsOwnOrgOrToEveryOrg()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var own = await SeedTenantAsync(database);
        var other = await SeedTenantAsync(database);
        var owner = _fixture.OwnerConnectionString(database);
        var app = _fixture.AppConnectionString(database);

        await SeedParserReleaseAsync(database, null, "global-parser");
        await SeedParserReleaseAsync(database, own.Org, "own-parser");
        await SeedParserReleaseAsync(database, other.Org, "other-parser");

        // The catalogue's read policy is what makes the guard answer differently per role: a built-in release is
        // every org's, a project-owned one is its own org's only.
        (await VisibleParsersAsync(database, own.Org, "global-parser")).ShouldBe(1);
        (await VisibleParsersAsync(database, own.Org, "own-parser")).ShouldBe(1);
        (await VisibleParsersAsync(database, other.Org, "own-parser")).ShouldBe(0);

        // Positive controls: a built-in release and this org's own project-owned release are both accepted.
        (await InsertAsync(owner, null, Baseline(own) with
        {
            Artifact = await NewArtifactAsync(database, own),
            ParserId = "global-parser",
            ParserVersion = DefaultParserVersion,
        })).ShouldBe(1);
        (await InsertAsync(owner, null, Baseline(own) with
        {
            Artifact = await NewArtifactAsync(database, own),
            ParserId = "own-parser",
            ParserVersion = DefaultParserVersion,
        })).ShouldBe(1);

        // Another org's project-owned release is refused on the owner connection (the catalogue is visible there,
        // so only the org predicate can refuse it) and on the app connection (row level security hides the row).
        var foreignOnOwner = await RefusedAsync(owner, null, Baseline(own) with
        {
            Artifact = await NewArtifactAsync(database, own),
            ParserId = "other-parser",
            ParserVersion = DefaultParserVersion,
        });
        foreignOnOwner.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        foreignOnOwner.ConstraintName.ShouldBe("raw_report_parser_invisible");

        var foreignOnApp = await RefusedAsync(app, own.Org, Baseline(own) with
        {
            Artifact = await NewArtifactAsync(database, own),
            ParserId = "other-parser",
            ParserVersion = DefaultParserVersion,
        });
        foreignOnApp.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        foreignOnApp.ConstraintName.ShouldBe("raw_report_parser_invisible");

        (await RowsAsync(database)).ShouldBe(2);
    }

    [Fact]
    public async Task RawReport_SecondParseOfTheSameRelease_IsRefusedAndOnConflictWritesNothing()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var tenant = await SeedTenantAsync(database);
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

    /// <summary>How many rows of one parser release the application role can see with that tenant's setting.</summary>
    private async Task<long> VisibleParsersAsync(string database, Guid org, string parserId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);

        return await scope.ScalarAsync<long>(
            "select count(*) from public.report_parser where parser_id = @parser_id", ("parser_id", parserId));
    }
}
