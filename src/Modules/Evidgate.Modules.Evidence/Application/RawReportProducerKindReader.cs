using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The one predicate for a producer kind <c>public.raw_report.producer_kind</c> can store: <c>^[a-z][a-z0-9-]*$</c> in
/// one to 64 characters. Both halves of the path use it — the lookup that decides what a payload carries, and the
/// refusal write that decides whether the row's column can hold it — so the read never hands on a kind the write would
/// have to drop. The shared forward pass carries the bounds and the later characters; its first class also admits a
/// digit, which this column does not, hence the explicit first-letter test.
/// </summary>
internal static class RawReportProducerKindShape
{
    /// <summary>The longest producer kind the column's CHECK admits.</summary>
    internal const int MaxLength = 64;

    /// <summary>True when the column's shape admits <paramref name="kind"/>.</summary>
    /// <param name="kind">The hint a manifest declared, or the kind a payload carries.</param>
    /// <returns>True when the kind is one the column stores.</returns>
    internal static bool IsStorable(string? kind) =>
        kind is { Length: > 0 } value
        && char.IsAsciiLetterLower(value[0])
        && AsciiShape.IsMatch(value, 1, MaxLength, AsciiShape.LowerAlnum, AsciiShape.LowerAlnumDash);
}

/// <summary>
/// What the run of a raw report declares as its producer kind: the one hint its manifest's shards agree on, or the
/// issue code that stood in its place. Every declared hint is classified here by the column's own shape, so a hint of
/// any length or spelling that shape cannot store is an issue, never a kind; a well-formed hint no parser registers is
/// still a resolved kind, because the parser registry, not this read, decides that it is <c>unsupported</c>. The hint
/// itself stays an untrusted lookup key the caller never logs or tags.
/// </summary>
/// <param name="Kind">
/// The single declared hint when the column's shape admits it, or <see langword="null"/> when the shards do not agree
/// on one or the one they declare is not storable.
/// </param>
/// <param name="Issue">
/// One of <see cref="ParseRawReportPayload.ProducerUnknown"/>, <see cref="ParseRawReportPayload.ProducerInvalid"/> or
/// <see cref="ParseRawReportPayload.ProducerAmbiguous"/>; <see langword="null"/> when the kind resolved.
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
            1 when RawReportProducerKindShape.IsStorable(hints[0]) => new RawReportProducerKind(hints[0], null),
            1 => new RawReportProducerKind(null, ParseRawReportPayload.ProducerInvalid),
            _ => new RawReportProducerKind(null, ParseRawReportPayload.ProducerAmbiguous),
        };
    }
}
