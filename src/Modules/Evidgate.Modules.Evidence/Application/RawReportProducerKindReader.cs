using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// What the run of a raw report declares as its producer kind: the one hint its manifest's shards agree on, or the
/// issue code that stood in its place. A well-formed hint no parser registers is still a resolved kind — the parser
/// registry, not this read, decides that it is <c>unsupported</c>.
/// </summary>
/// <param name="Kind">The single declared hint, or <see langword="null"/> when the shards do not agree on one.</param>
/// <param name="Issue">
/// One of <see cref="ParseRawReportPayload.ProducerUnknown"/> or <see cref="ParseRawReportPayload.ProducerAmbiguous"/>;
/// <see langword="null"/> when the kind resolved.
/// </param>
internal sealed record RawReportProducerKind(string? Kind, string? Issue)
{
    /// <summary>True when the manifest declared exactly one producer kind.</summary>
    public bool IsResolved => Kind is not null;
}

/// <summary>
/// The server-side source of a raw report's producer kind: the run's manifest declares it per shard, so the completion
/// reads it instead of trusting a client-declared dialect.
/// </summary>
internal interface IRawReportProducerKindReader
{
    /// <summary>Resolves the one producer kind the run's manifest declares, or the issue code standing in its place.</summary>
    /// <param name="runId">The run the report was uploaded for.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<RawReportProducerKind> ReadAsync(Guid runId, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IRawReportProducerKindReader"/> over the caller's tenant transaction. It never opens a connection or a
/// transaction and never commits: the read joins the run to its manifest on <c>org_id</c> and <c>manifest_id</c>, so
/// row level security and the join both keep another tenant's manifest out of reach.
/// </summary>
/// <param name="db">The Dapper path over the ambient scope; it demands an active scope before it touches the database.</param>
internal sealed class RawReportProducerKindReader(ITenantDb db) : IRawReportProducerKindReader
{
    /// <inheritdoc />
    public async ValueTask<RawReportProducerKind> ReadAsync(Guid runId, CancellationToken cancellationToken)
    {
        var hints = await db
            .QueryAsync<string>(
                RawReportProducerKindSql.ProducerKinds,
                new { run_id = runId },
                cancellationToken)
            .ConfigureAwait(false);

        return hints.Count switch
        {
            0 => new RawReportProducerKind(null, ParseRawReportPayload.ProducerUnknown),
            1 => new RawReportProducerKind(hints[0], null),
            _ => new RawReportProducerKind(null, ParseRawReportPayload.ProducerAmbiguous),
        };
    }
}
