namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The synthetic producer identity of one reprocessed report. It is derived from the request, never from the artifact,
/// so the events of a reprocess cannot land on the identities the original report's producer already holds for the same
/// run, and a replay of the task derives the same identity again.
/// </summary>
internal static class ReprocessReportProducer
{
    /// <summary>The prefix that marks a producer as a reprocess of one request rather than a report an ingest route accepted.</summary>
    private const string Prefix = "reprocess-";

    /// <summary>The producer id of one request: <c>reprocess-&lt;parsing id&gt;</c>.</summary>
    /// <param name="parsingId">The request row, which is also the id of the report row the task writes.</param>
    /// <returns>A producer id of the column's own shape (lower case, digits and dashes).</returns>
    public static string IdFor(Guid parsingId) => Prefix + parsingId.ToString("D");
}
