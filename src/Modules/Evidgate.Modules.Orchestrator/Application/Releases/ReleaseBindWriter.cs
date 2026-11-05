using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;

namespace Evidgate.Modules.Orchestrator.Application.Releases;

/// <summary>
/// The one writer of a binding's audit row and outbox event, shared by the two paths that can create a binding: the
/// explicit route (<c>POST /v1/releases/{id}/runs</c>) and the run create that names a <c>release_id</c>. Both must
/// leave the same evidence, or the audit chain would say a binding happened only when it was made by hand.
/// </summary>
internal static class ReleaseBindWriter
{
    /// <summary>The action both paths record, and the outbox task type an applied binding enqueues.</summary>
    internal const string Action = "release.bind";

    /// <summary>The outbox task type of an applied binding: no handler consumes it yet, and an unclaimed row stays queued.</summary>
    internal const string TaskType = "orchestrator.release.bound";

    /// <summary>
    /// Describes the release a binding changed: allowlisted keys only, and never a component key or digest. The explicit
    /// route lets the pipeline record the action from its own attribute; the run-create route records it explicitly
    /// through <see cref="Record"/>, because there the ambient action is <c>run.create</c>.
    /// </summary>
    /// <param name="audit">The unit-of-work audit buffer.</param>
    /// <param name="releaseId">The release the row is about.</param>
    /// <param name="runId">The run the binding is for.</param>
    /// <param name="boundCount">The rows the pair holds after the write.</param>
    /// <param name="mismatchedCount">The release components the run does not match.</param>
    internal static void Describe(IAuditTrail audit, Guid releaseId, Guid runId, int boundCount, int mismatchedCount) =>
        audit.Describe(new AuditObject(
            "release",
            releaseId.ToString("D"),
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = boundCount.ToString(CultureInfo.InvariantCulture),
                ["outcome"] = mismatchedCount == 0 ? "complete" : "partial",
                ["run_id"] = runId.ToString("D"),
            }));

    /// <summary>
    /// Queues the <c>release.bind</c> entry itself. Only a path whose ambient action is a different one calls this: a
    /// second call from the explicit route would duplicate the row the pipeline already records.
    /// </summary>
    /// <param name="audit">The unit-of-work audit buffer, already described by <see cref="Describe"/>.</param>
    /// <param name="actor">The principal that asked for the binding.</param>
    internal static void Record(IAuditTrail audit, AuditActor actor) => audit.Record(Action, actor);

    /// <summary>
    /// The outbox event of an applied binding, deduped on the matched key set: a partial re-bind that adds keys gets its
    /// own row, while a replay of the same set collides with this one.
    /// </summary>
    /// <param name="outbox">The outbox writer of the ambient tenant transaction.</param>
    /// <param name="releaseId">The release that was bound.</param>
    /// <param name="runId">The run that was bound.</param>
    /// <param name="boundKeys">The component keys the pair now holds, in key order.</param>
    /// <param name="mismatchedCount">The release components the run does not match.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async ValueTask EnqueueAsync(
        IOutboxWriter outbox,
        Guid releaseId,
        Guid runId,
        IReadOnlyList<string> boundKeys,
        int mismatchedCount,
        CancellationToken cancellationToken) =>
        await outbox.EnqueueAsync(
            TaskType,
            JsonSerializer.Serialize(new
            {
                release_id = releaseId,
                run_id = runId,
                bound = boundKeys.Count,
                mismatched = mismatchedCount,
            }),
            "release",
            releaseId,
            OutboxKey(releaseId, runId, boundKeys),
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The dedupe key of one matched set: <c>orchestrator:release:&lt;id&gt;:run:&lt;id&gt;:&lt;digest&gt;</c>, the digest
    /// covering the ordered keys. It stays inside the column's 256-character bound.
    /// </summary>
    /// <param name="releaseId">The release that was bound.</param>
    /// <param name="runId">The run that was bound.</param>
    /// <param name="boundKeys">The component keys the pair now holds, in key order.</param>
    internal static string OutboxKey(Guid releaseId, Guid runId, IReadOnlyList<string> boundKeys)
    {
        var keys = string.Join('\u001f', boundKeys);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keys)))[..32];
        return $"orchestrator:release:{releaseId:D}:run:{runId:D}:{digest}";
    }
}
