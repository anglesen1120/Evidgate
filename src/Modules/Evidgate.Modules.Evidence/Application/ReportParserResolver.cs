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
/// The one read of the parser registry (04.4.1-T1.1): <c>producer_kind -&gt; (parser_id, version)</c>. It runs on the
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
internal sealed class ReportParserResolver(ITenantDb db) : IReportParserResolver
{
    /// <summary>
    /// One row per producer kind. A built-in row (<c>org_id is null</c>) is the platform default; a tenant row wins over
    /// it, because a project that registered its own dialect must not be overruled by the platform seed. Among rows of
    /// equal scope the most recently registered release wins, so a parser upgrade is a new row and a re-parse picks it
    /// up; stored events keep the version they were parsed with. The ordering is total (<c>id</c> breaks a tie), so two
    /// runs of the same read cannot disagree.
    /// </summary>
    internal const string SelectSql = """
        select parser_id as "ParserId", version as "Version"
          from public.report_parser
         where producer_kind = @producer_kind
         order by (org_id is not null) desc, created_at desc, version desc, id
         limit 1
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

        var row = await db
            .QuerySingleOrDefaultAsync<ParserRow>(SelectSql, new { producer_kind = producerKind }, cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? ReportParserResolution.Unsupported(UnknownProducerKind)
            : ReportParserResolution.Resolved(row.ParserId, row.Version);
    }

    /// <summary>The two stored columns, before they become the resolution record.</summary>
    /// <param name="ParserId">The registry's <c>parser_id</c>.</param>
    /// <param name="Version">The registry's <c>version</c>.</param>
    private sealed record ParserRow(string ParserId, string Version);
}
