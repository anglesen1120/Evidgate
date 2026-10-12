// 04.1.1-T1 (F3): the ingest handler keeps a local copy of 05.3.3's status normalisation because the
// Orchestrator domain type is not reachable across the module edge at runtime. This parity test pins the copy
// to the Orchestrator's CaseAttemptStatus.Normalize over the whole vocabulary — the closed table of
// contracts/adapter/playwright-mapping.md §status plus the values an unknown producer may send — so the two can
// never drift. No Docker: it is a pure function comparison.
using Evidgate.Modules.Ingestion.Application.Commands;
using Evidgate.Modules.Orchestrator.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>Parity of the ingest handler's local <c>Normalize</c> with the Orchestrator's <c>CaseAttemptStatus</c>.</summary>
public sealed class IngestEventBatchNormalizeParityTests
{
    /// <summary>
    /// Every raw status the vocabulary covers, plus the values an unknown producer may send, each with and
    /// without <c>xfail</c>.
    /// </summary>
    public static TheoryData<string?, bool> Vocabulary()
    {
        string?[] raws =
        [
            null, "", " ", "passed", "failed", "timedOut", "timed_out", "skipped", "interrupted", "error",
            "blocked", "PASSED", "Failed", "TIMEDOUT", "constructor", "__proto__", "no_such_status",
        ];

        var data = new TheoryData<string?, bool>();
        foreach (var raw in raws)
        {
            data.Add(raw, false);
            data.Add(raw, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Vocabulary))]
    public void Local_normalize_matches_orchestrator(string? raw, bool xfail) =>
        IngestEventBatchHandler.Normalize(raw, xfail).ShouldBe(CaseAttemptStatus.Normalize(raw, xfail));
}
