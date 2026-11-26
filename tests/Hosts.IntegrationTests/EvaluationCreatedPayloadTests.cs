using System.Text.Json;
using Evidgate.Modules.Policy.Application.Evaluation;
using Evidgate.Modules.Policy.Webhooks.Application;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

public sealed class EvaluationCreatedPayloadTests
{
    private static readonly string[] PayloadMembers =
    [
        "evaluation_id", "run_id", "evidence_revision", "policy_revision_id", "policy_hash", "manifest_hash", "exceptions_hash",
        "decision", "reasons",
    ];

    [Fact]
    public void From_EvaluationRow_EmitsOnlyIdentifiersHashesAndReasonCodes()
    {
        var row = new EvaluationRecord(Guid.NewGuid(), Guid.NewGuid(), 2, Guid.NewGuid(), "policy", "manifest",
            "complete", [], "has_failures", "FAIL", ["required_assertion_failed"], "do-not-send", "live", "exceptions",
            DateTimeOffset.UnixEpoch, [], DateTimeOffset.UnixEpoch);
        using var json = JsonDocument.Parse(EvaluationCreatedPayload.From(row).ToUtf8Bytes());
        var root = json.RootElement;
        root.EnumerateObject().Select(p => p.Name).ToArray().ShouldBe(PayloadMembers);
        root.GetProperty("evaluation_id").GetGuid().ShouldBe(row.Id);
        root.GetProperty("run_id").GetGuid().ShouldBe(row.RunId);
        root.GetProperty("policy_revision_id").GetGuid().ShouldBe(row.PolicyRevisionId);
        root.GetProperty("policy_hash").GetString().ShouldBe(row.PolicyHash);
        root.GetProperty("manifest_hash").GetString().ShouldBe(row.ManifestHash);
        root.GetProperty("exceptions_hash").GetString().ShouldBe(row.ExceptionsHash);
        root.GetProperty("evidence_revision").GetInt32().ShouldBe(2);
        root.GetProperty("decision").GetString().ShouldBe("FAIL");
        root.GetProperty("reasons")[0].GetString().ShouldBe("required_assertion_failed");
    }
}
