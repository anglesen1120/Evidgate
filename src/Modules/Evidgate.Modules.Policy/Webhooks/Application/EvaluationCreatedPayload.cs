using System.Buffers;
using System.Text.Json;
using Evidgate.Modules.Policy.Application.Evaluation;

namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>
/// The <c>evaluation.created</c> body: identifiers, hashes, the decision token and reason codes. No artifact content,
/// no case names, no secrets, so the receiver can gate on it without learning customer data.
/// </summary>
/// <param name="EvaluationId">The evaluation id; also <c>Evidgate-Event-Id</c>.</param>
/// <param name="RunId">The run judged.</param>
/// <param name="EvidenceRevision">The frozen revision judged.</param>
/// <param name="PolicyRevisionId">The pinned policy revision.</param>
/// <param name="PolicyHash">Its content address.</param>
/// <param name="ManifestHash">The manifest the evidence froze against.</param>
/// <param name="ExceptionsHash">The exception snapshot the decision was made under.</param>
/// <param name="Decision">PASS, FAIL or INCONCLUSIVE.</param>
/// <param name="Reasons">The closed reason codes.</param>
internal sealed record EvaluationCreatedPayload(
    Guid EvaluationId,
    Guid RunId,
    int EvidenceRevision,
    Guid PolicyRevisionId,
    string PolicyHash,
    string ManifestHash,
    string ExceptionsHash,
    string Decision,
    IReadOnlyList<string> Reasons)
{
    /// <summary>The event type stored on the outbox row, the delivery row and the header.</summary>
    public const string EventType = "evaluation.created";

    /// <summary>Maps a stored evaluation; fields outside the payload are dropped by construction.</summary>
    /// <param name="row">The evaluation.</param>
    internal static EvaluationCreatedPayload From(EvaluationRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new(
            row.Id, row.RunId, row.EvidenceRevision, row.PolicyRevisionId, row.PolicyHash, row.ManifestHash,
            row.ExceptionsHash, row.Decision, row.Reasons);
    }

    /// <summary>The compact UTF-8 JSON that is signed and posted, member order fixed.</summary>
    public byte[] ToUtf8Bytes()
    {
        var buffer = new ArrayBufferWriter<byte>(512);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("evaluation_id", EvaluationId);
            writer.WriteString("run_id", RunId);
            writer.WriteNumber("evidence_revision", EvidenceRevision);
            writer.WriteString("policy_revision_id", PolicyRevisionId);
            writer.WriteString("policy_hash", PolicyHash);
            writer.WriteString("manifest_hash", ManifestHash);
            writer.WriteString("exceptions_hash", ExceptionsHash);
            writer.WriteString("decision", Decision);
            writer.WriteStartArray("reasons");

            foreach (var reason in Reasons)
            {
                writer.WriteStringValue(reason);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
