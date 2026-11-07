using System.Numerics;
using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The parser a raw report's producer kind maps to, or the named reason no parser is registered for it.
/// <para>
/// The pair is what a parse stamps on every event it writes (<c>ext.parser = "&lt;parser_id&gt;/&lt;version&gt;"</c>), so a
/// reprocess under a newer parser adds a second, distinguishable set instead of overwriting the first (D4). An
/// unresolvable kind is not an error: a producer the platform has never mapped is reported as <c>unsupported</c> and
/// writes no event.
/// </para>
/// </summary>
internal sealed record ReportParserResolution
{
    private ReportParserResolution(string? parserId, string? version, string? unsupportedReason)
    {
        ParserId = parserId;
        Version = version;
        UnsupportedReason = unsupportedReason;
    }

    /// <summary>The registry's <c>parser_id</c>, or <see langword="null"/> when the kind is unsupported.</summary>
    public string? ParserId { get; }

    /// <summary>The registry's <c>version</c>, or <see langword="null"/> when the kind is unsupported.</summary>
    public string? Version { get; }

    /// <summary>The code word naming why the kind is unsupported (<c>unknown_producer_kind</c>), or <see langword="null"/>.</summary>
    public string? UnsupportedReason { get; }

    /// <summary>True when a parser pair was found.</summary>
    public bool IsResolved => ParserId is not null;

    /// <summary>A registered parser pair.</summary>
    /// <param name="parserId">The registry's <c>parser_id</c>.</param>
    /// <param name="version">The registry's <c>version</c>.</param>
    public static ReportParserResolution Resolved(string parserId, string version) => new(parserId, version, null);

    /// <summary>The named miss: a producer kind no registry row names.</summary>
    /// <param name="reason">The code word the parse reports, never a producer value.</param>
    public static ReportParserResolution Unsupported(string reason) => new(null, null, reason);
}

/// <summary>
/// The one read of the parser registry: <c>producer_kind -&gt; (parser_id, version)</c>. It runs on the
/// caller's ambient tenant scope, because the catalogue's built-in rows are readable by every org and an org's own
/// override must stay invisible to another.
/// </summary>
internal interface IReportParserResolver
{
    /// <summary>Resolves the parser a producer kind names.</summary>
    /// <param name="producerKind">The kind a raw report declares, e.g. <c>pytest-junitxml</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The registered pair, or a named <c>unsupported</c> result.</returns>
    ValueTask<ReportParserResolution> ResolveAsync(string producerKind, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IReportParserResolver"/> over Dapper (00 §5.5 C5). It never opens a connection or a transaction and never
/// commits: the one statement runs in the scope the caller already opened.
/// </summary>
/// <param name="db">The Dapper path over the ambient scope; it demands an active scope before touching the database.</param>
/// <param name="scopes">The ambient scope, read for the org the statement restates beside forced row security.</param>
internal sealed class ReportParserResolver(ITenantDb db, ITenantScopeAccessor scopes) : IReportParserResolver
{
    /// <summary>
    /// Every release the caller can see for the kind, widest scope first and the newest registration before an older
    /// one. The candidates come back unsorted by version because <c>1.10</c> is newer than <c>1.9</c> and a
    /// pre-release has no place in that order: <see cref="ParserVersionOrder"/> ranks them, as a reprocess does.
    /// </summary>
    internal const string SelectSql = """
        select parser_id as "ParserId", version as "Version", (org_id is not null) as "TenantOwned"
          from public.report_parser
         where producer_kind = @producer_kind
           and (org_id is null or org_id = @org_id)
         order by (org_id is not null) desc, created_at desc, id
        """;

    /// <summary>The code word an unsupported producer kind carries; it names the registry, never the producer's text.</summary>
    internal const string UnknownProducerKind = "unknown_producer_kind";

    /// <inheritdoc />
    public async ValueTask<ReportParserResolution> ResolveAsync(
        string producerKind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(producerKind))
        {
            // Nothing to look up: the payload named no kind at all.
            return ReportParserResolution.Unsupported(UnknownProducerKind);
        }

        // The kind is bound as a parameter, never composed into the statement: it is the report's own declared text.
        // The org is restated here so a connection that bypasses row security still sees only its own overrides.
        var rows = await db
            .QueryAsync<ParserRow>(SelectSql, new { producer_kind = producerKind, org_id = scopes.Require().OrgId }, cancellationToken)
            .ConfigureAwait(false);

        var release = HighestRelease(VisibleScope(rows));

        return release is null
            ? ReportParserResolution.Unsupported(UnknownProducerKind)
            : ReportParserResolution.Resolved(release.ParserId, release.Version);
    }

    /// <summary>
    /// The releases that may compete: a project-owned row outranks the platform default, and every row of the winning
    /// scope competes because which of them wins is a version question. The read puts the wider scope first, so the
    /// scope is the run of rows sharing the first row's ownership.
    /// </summary>
    /// <param name="rows">The caller's visible releases for one kind, widest scope first.</param>
    private static IReadOnlyList<ParserRow> VisibleScope(IReadOnlyList<ParserRow> rows) =>
        rows.Count == 0 ? [] : [.. rows.TakeWhile(row => row.TenantOwned == rows[0].TenantOwned)];

    /// <summary>
    /// The release a parse runs under: the highest under the dotted numeric order, so an upgrade is a new row and every
    /// read agrees with the reprocess route. A release the order cannot place is never chosen — a scope that holds
    /// nothing else answers an unsupported kind — and releases that name the same version keep the read's order, the
    /// newest registration first, so the choice is total.
    /// </summary>
    /// <param name="candidates">The releases of the winning scope, the most recent registration first.</param>
    private static ParserRow? HighestRelease(IReadOnlyList<ParserRow> candidates)
    {
        ParserRow? highest = null;
        BigInteger[] highestSegments = [];

        foreach (var candidate in candidates)
        {
            if (!ParserVersionOrder.TryParse(candidate.Version, out var segments))
            {
                // Unplaceable: no rule ranks it against a release it cannot be compared with, so it never parses.
                continue;
            }

            if (highest is null || ParserVersionOrder.CompareSegments(segments, highestSegments) > 0)
            {
                highest = candidate;
                highestSegments = segments;
            }
        }

        return highest;
    }

    /// <summary>The stored columns the selection reads, and the scope the row belongs to.</summary>
    /// <param name="ParserId">The registry's <c>parser_id</c>.</param>
    /// <param name="Version">The registry's <c>version</c>.</param>
    /// <param name="TenantOwned">True for a project-owned row, false for the platform default.</param>
    private sealed record ParserRow(string ParserId, string Version, bool TenantOwned);
}
