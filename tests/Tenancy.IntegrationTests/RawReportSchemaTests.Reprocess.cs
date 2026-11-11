// The parser-release half of the `raw_report_reprocess` schema (migration 0081): a request pins the catalogue row
// that will parse the report again, and the foreign key alone compares the catalogue's global
// `(parser_id, version)` key, so it would accept another org's project-owned release. A built-in release is every
// org's, a project-owned one is its own org's only, and the refusal names `raw_report_reprocess_parser_invisible` on
// SQLSTATE 23514. The request row names the same world the `raw_report` cases seed: this org's committed run,
// artifact and source report, so only the release differs between an accepted and a refused request.
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

// Dedicated database: the release case counts the whole request table and seeds built-in parser rows.
public sealed partial class RawReportSchemaTests
{
    private const string RequestTable = "raw_report_reprocess";
    private const string RequestGuard = "raw_report_reprocess_parser_visible";
    private const string RequestInvisible = "raw_report_reprocess_parser_invisible";

    [Fact]
    public async Task ReprocessRequest_ParserRelease_IsVisibleToItsOwnOrgOrToEveryOrg()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var own = await SeedTenantAsync(database);
        var other = await SeedTenantAsync(database);
        var owner = _fixture.OwnerConnectionString(database);
        var app = _fixture.AppConnectionString(database);
        var source = await SeedSourceReportAsync(database, own);

        await SeedParserReleaseAsync(database, null, "global-parser");
        await SeedParserReleaseAsync(database, own.Org, "own-parser");
        await SeedParserReleaseAsync(database, other.Org, "other-parser");

        // The catalogue's read policy is what makes the guard answer differently per role: a built-in release is
        // every org's, a project-owned one is its own org's only.
        (await VisibleParsersAsync(database, own.Org, "global-parser")).ShouldBe(1);
        (await VisibleParsersAsync(database, own.Org, "own-parser")).ShouldBe(1);
        (await VisibleParsersAsync(database, own.Org, "other-parser")).ShouldBe(0);
        (await VisibleParsersAsync(database, other.Org, "own-parser")).ShouldBe(0);

        // Positive controls: a built-in release and this org's own project-owned release are both accepted, on the
        // owner connection and on the application role whose row level security check passes on an own-org request.
        (await InsertRequestAsync(
            owner, null, Request(own, source, await NewArtifactAsync(database, own), "global-parser"))).ShouldBe(1);
        (await InsertRequestAsync(
            app, own.Org, Request(own, source, await NewArtifactAsync(database, own), "own-parser"))).ShouldBe(1);

        // Another org's project-owned release is refused on the owner connection (the catalogue is visible there, so
        // only the org predicate can refuse it) and on the app connection (row level security hides the row).
        var foreignOnOwner = await RefusedRequestAsync(
            owner, null, Request(own, source, await NewArtifactAsync(database, own), "other-parser"));
        foreignOnOwner.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        foreignOnOwner.ConstraintName.ShouldBe(RequestInvisible);

        var foreignOnApp = await RefusedRequestAsync(
            app, own.Org, Request(own, source, await NewArtifactAsync(database, own), "other-parser"));
        foreignOnApp.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        foreignOnApp.ConstraintName.ShouldBe(RequestInvisible);

        // A pair in no catalogue at all is refused by the same guard, not by the foreign key: the BEFORE INSERT
        // trigger answers before the reference is checked, so the refusal names the visibility rule.
        var unknown = await RefusedRequestAsync(
            app, own.Org, Request(own, source, await NewArtifactAsync(database, own), "no-such-parser"));
        unknown.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        unknown.ConstraintName.ShouldBe(RequestInvisible);

        (await RequestRowsAsync(database)).ShouldBe(2);
    }

    [Fact]
    public async Task ReprocessRequest_Guard_IsTheNamedBeforeInsertRowTrigger()
    {
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from pg_catalog.pg_trigger where tgname = '{RequestGuard}' "
            + $"and tgrelid = 'public.{RequestTable}'::regclass and not tgisinternal")).ShouldBe(
            1,
            "one guard trigger answers an insert, and an update or delete meets the immutability trigger instead");

        // The relation is pinned in the query above; the definition itself is formatted against this connection's
        // search_path, so the table may print unqualified — the trigger kind and function are what this case pins.
        var definition = await _fixture.ScalarAsync<string>(
            database,
            $"select pg_get_triggerdef(oid) from pg_catalog.pg_trigger where tgname = '{RequestGuard}' "
            + $"and tgrelid = 'public.{RequestTable}'::regclass and not tgisinternal");

        definition.ShouldContain("BEFORE INSERT ON");
        definition.ShouldContain("FOR EACH ROW");
        definition.ShouldContain("raw_report_reprocess_parser_guard()");
    }

    /// <summary>One <c>raw_report_reprocess</c> row's members: the id is the answer's <c>parsing_id</c>.</summary>
    private sealed record RequestRow
    {
        internal required Guid Org { get; init; }

        internal required Guid Run { get; init; }

        internal required Guid Artifact { get; init; }

        internal required Guid SourceReport { get; init; }

        internal required string ParserId { get; init; }

        internal string ParserVersion { get; init; } = DefaultParserVersion;

        internal Guid Id { get; init; } = Guid.CreateVersion7();
    }

    private static RequestRow Request(Tenant tenant, Guid source, Guid artifact, string parserId) =>
        new()
        {
            Org = tenant.Org,
            Run = tenant.Run,
            Artifact = artifact,
            SourceReport = source,
            ParserId = parserId,
        };

    private static (string Sql, (string Name, object Value)[] Parameters) InsertRequest(RequestRow row) =>
        ("insert into public.raw_report_reprocess "
            + "(id, org_id, run_id, artifact_id, source_raw_report_id, parser_id, parser_version) "
            + "values (@id, @org, @run, @artifact, @source, @parser_id, @parser_version)",
            new (string Name, object Value)[]
            {
                ("id", row.Id),
                ("org", row.Org),
                ("run", row.Run),
                ("artifact", row.Artifact),
                ("source", row.SourceReport),
                ("parser_id", row.ParserId),
                ("parser_version", row.ParserVersion),
            });

    /// <summary>Runs one request insert in its own committed transaction and returns the rows it wrote.</summary>
    private static async Task<int> InsertRequestAsync(string connectionString, Guid? org, RequestRow row)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, org);
        var (sql, parameters) = InsertRequest(row);
        var affected = await scope.ExecuteAsync(sql, parameters);
        await scope.CommitAsync();

        return affected;
    }

    /// <summary>Runs one request insert that must be refused, in a transaction that is rolled back.</summary>
    private static async Task<PostgresException> RefusedRequestAsync(
        string connectionString,
        Guid? org,
        RequestRow row)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, org);
        var (sql, parameters) = InsertRequest(row);

        return await scope.CaptureErrorAsync(sql, parameters);
    }

    /// <summary>The report the original parse committed, written as the owner under the built-in release.</summary>
    private async Task<Guid> SeedSourceReportAsync(string database, Tenant tenant)
    {
        var id = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        (await scope.ExecuteAsync(
            """
            insert into public.raw_report
                (id, org_id, run_id, artifact_id, producer_kind, parser_id, parser_version, outcome, reason,
                 declared_tests, observed_tests, observed_failures, discrepancies)
            values (@id, @org, @run, @artifact, @producer, @parser_id, @parser_version, 'mapped', null,
                    null, 0, 0, '[]'::jsonb)
            """,
            ("id", id),
            ("org", tenant.Org),
            ("run", tenant.Run),
            ("artifact", tenant.Artifact),
            ("producer", DefaultProducerKind),
            ("parser_id", DefaultParserId),
            ("parser_version", DefaultParserVersion))).ShouldBe(1);
        await scope.CommitAsync();

        return id;
    }

    private async Task<long> RequestRowsAsync(string database) =>
        await _fixture.ScalarAsync<long>(database, $"select count(*) from public.{RequestTable}");
}
