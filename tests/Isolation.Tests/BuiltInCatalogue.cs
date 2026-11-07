using Evidgate.Testing;

namespace Isolation.Tests;

/// <summary>
/// The migration-seeded rows a reset-based suite needs back. The harness reset
/// (<see cref="RespawnCheckpoint"/>) truncates every application table, migration seed blocks included, so the
/// built-in reference data a later migration's guard reads is gone before each test of
/// <see cref="HarnessTest"/>. A suite that generates a row naming one restores it first through the owner seam
/// (<see cref="DatabaseFixture.SeedAsync"/>) — never through the app role, which 0075 leaves with SELECT only.
/// <para>
/// The parser pair is the first entry because <c>raw_report_reprocess</c> (0081) carries a NOT NULL release the
/// application role cannot mint, so <see cref="TenantRowFactory"/> pins the built-in release
/// (<see cref="ReportParserId"/>/<see cref="ReportParserVersion"/>) and the guard
/// <c>raw_report_reprocess_parser_visible</c> reads that row back as the caller.
/// </para>
/// </summary>
internal static class BuiltInCatalogue
{
    /// <summary>The parser id <c>migrations/0075_ingestion_report_parser.sql</c> seeds.</summary>
    internal const string ReportParserId = "junit-pytest";

    /// <summary>The version of the release <see cref="ReportParserId"/> names.</summary>
    internal const string ReportParserVersion = "1.0.0";

    /// <summary>
    /// Restores the built-in pytest release exactly as 0075 seeds it — the migration's fixed id, a NULL
    /// <c>org_id</c> (what makes it visible to every org), the label and the note — so the database is back to the
    /// state a freshly migrated one has.
    /// </summary>
    internal static Task SeedReportParserAsync(DatabaseFixture fixture, CancellationToken cancellationToken) =>
        fixture.SeedAsync(
            """
            insert into public.report_parser (id, org_id, parser_id, version, producer_kind, schema_version, notes)
            values (@id, null, @parser_id, @version, 'pytest-junitxml', 1,
                    'pytest junitxml: testcase -> test.finished, classname/name identity hint, declared vs observed counts')
            on conflict (parser_id, version) do nothing
            """,
            [
                ("id", Guid.Parse("018f8f5e-0000-7000-8000-000000000075")),
                ("parser_id", ReportParserId),
                ("version", ReportParserVersion),
            ],
            cancellationToken);
}
