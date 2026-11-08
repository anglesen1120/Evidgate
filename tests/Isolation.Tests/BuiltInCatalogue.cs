namespace Isolation.Tests;

/// <summary>
/// The migration-seeded report parser release that <c>raw_report_reprocess</c> (0081) rows name: its NOT NULL
/// release cannot be minted by the application role, so <see cref="TenantRowFactory"/> pins the built-in one. The
/// harness reset keeps it (<see cref="Evidgate.Testing.RespawnCheckpoint"/>), so no suite restores it.
/// </summary>
internal static class BuiltInCatalogue
{
    /// <summary>The parser id <c>migrations/0075_ingestion_report_parser.sql</c> seeds.</summary>
    internal const string ReportParserId = "junit-pytest";

    /// <summary>The version of the release <see cref="ReportParserId"/> names.</summary>
    internal const string ReportParserVersion = "1.0.0";
}
