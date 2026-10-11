using Evidgate.Modules.Orchestrator.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The pure status normalisation of 05.3.3-T1.2 (00 §8.3, ruling T3) without a database: the exact, case-sensitive
/// raw vocabulary of <c>contracts/adapter/playwright-mapping.md</c> §status, the <c>xfail</c> fold (coordinator
/// decision S29; the S18 owner-confirmed unexpected-pass decision of 2026-10-10) and the fail-closed <c>error</c>
/// fallback for anything else. The DB-backed <c>WriteAttempt_*</c> cases belong to 05.3.3-T2 and are not
/// duplicated here.
/// </summary>
public sealed class CaseAttemptStatusTests
{
    /// <summary>Every row of the mapping table, with <c>xfail</c> false and true (05.3.3 §10 T1 Notes).</summary>
    public static TheoryData<string, bool, string> MappingTable => new()
    {
        { "passed", false, CaseAttemptStatus.Passed },
        { "passed", true, CaseAttemptStatus.Failed },
        { "failed", false, CaseAttemptStatus.Failed },
        { "failed", true, CaseAttemptStatus.Passed },
        { "timedOut", false, CaseAttemptStatus.TimedOut },
        { "timedOut", true, CaseAttemptStatus.TimedOut },
        { "timed_out", false, CaseAttemptStatus.TimedOut },
        { "timed_out", true, CaseAttemptStatus.TimedOut },
        { "skipped", false, CaseAttemptStatus.Skipped },
        { "skipped", true, CaseAttemptStatus.Skipped },
        { "interrupted", false, CaseAttemptStatus.Error },
        { "interrupted", true, CaseAttemptStatus.Error },
        { "error", false, CaseAttemptStatus.Error },
        { "error", true, CaseAttemptStatus.Error },
    };

    /// <summary>Raw values outside the table: every one maps to <c>error</c>, never dropped (05.3.3-T1.2).</summary>
    public static TheoryData<string?> UnknownRawStatuses => new()
    {
        null,
        "",
        " passed",
        "PASSED",
        "Passed",
        "timedout",
        "blocked",
        "constructor",
        "__proto__",
        "skipped\n",
        new string('a', 64),
    };

    /// <summary>Raw values whose mapping <c>xfail</c> must not change (only <c>passed</c>/<c>failed</c> fold).</summary>
    public static TheoryData<string?> XfailInvariantRawStatuses => new()
    {
        "timedOut",
        "timed_out",
        "skipped",
        "interrupted",
        "error",
        null,
        "",
        " passed",
        "PASSED",
        "Passed",
        "timedout",
        "blocked",
        "constructor",
        "__proto__",
        "skipped\n",
        new string('a', 64),
    };

    /// <summary>Every raw value this suite exercises, for the closed-set check.</summary>
    public static TheoryData<string?> EveryRawStatus => new()
    {
        null,
        "",
        " passed",
        "PASSED",
        "Passed",
        "timedout",
        "blocked",
        "constructor",
        "__proto__",
        "skipped\n",
        new string('a', 64),
        "passed",
        "failed",
        "timedOut",
        "timed_out",
        "skipped",
        "interrupted",
        "error",
    };

    /// <summary>Every mapping-table row, with and without the <c>xfail</c> fold.</summary>
    [Theory]
    [MemberData(nameof(MappingTable))]
    public void Normalize_KnownRawStatus_MapsByTheTable(string raw, bool xfail, string expected)
    {
        CaseAttemptStatus.Normalize(raw, xfail).ShouldBe(expected);
    }

    /// <summary>
    /// A raw value outside the table — null, empty or whitespace, a different case, a prototype name, a future
    /// producer value or an over-long string — maps to <c>error</c> and is never dropped, with or without
    /// <c>xfail</c> (05.3.3-T1.2).
    /// </summary>
    [Theory]
    [MemberData(nameof(UnknownRawStatuses))]
    public void Normalize_UnknownRawStatus_NormalizedError(string? raw)
    {
        CaseAttemptStatus.Normalize(raw).ShouldBe(CaseAttemptStatus.Error);
        CaseAttemptStatus.Normalize(raw, xfail: true).ShouldBe(CaseAttemptStatus.Error);
    }

    /// <summary>
    /// <c>xfail</c> changes only the two expected-failure rows: for every other raw value — including an unknown
    /// one — the result is the plain mapping (coordinator decision S29; S18 owner decision).
    /// </summary>
    [Theory]
    [MemberData(nameof(XfailInvariantRawStatuses))]
    public void Normalize_XfailOnNonFailPassRawStatus_DoesNotChangeThePlainMapping(string? raw)
    {
        CaseAttemptStatus.Normalize(raw, xfail: true).ShouldBe(CaseAttemptStatus.Normalize(raw));
    }

    /// <summary>The result is always one of the six closed values, whatever the raw value (00 §8.3).</summary>
    [Theory]
    [MemberData(nameof(EveryRawStatus))]
    public void Normalize_AnyRawStatus_ReturnsAValueInAll(string? raw)
    {
        CaseAttemptStatus.Normalize(raw).ShouldBeOneOf(CaseAttemptStatus.All);
        CaseAttemptStatus.Normalize(raw, xfail: true).ShouldBeOneOf(CaseAttemptStatus.All);
    }

    /// <summary>
    /// The normaliser never echoes a raw value that is not itself a normalised value: an unrecognised raw is
    /// replaced by <c>error</c>, never returned as-is.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnknownRawStatuses))]
    public void Normalize_UnknownRawStatus_NeverEchoesTheRawValue(string? raw)
    {
        var normalized = CaseAttemptStatus.Normalize(raw);

        normalized.ShouldBe(CaseAttemptStatus.Error);
        if (raw is not null)
        {
            normalized.ShouldNotBe(raw);
        }
    }

    /// <summary>The closed set is exactly the six 00 §8.3 values, in contract order.</summary>
    [Fact]
    public void All_IsTheSixClosedValues()
    {
        CaseAttemptStatus.All.ShouldBe(
            [CaseAttemptStatus.Passed, CaseAttemptStatus.Failed, CaseAttemptStatus.Skipped, CaseAttemptStatus.TimedOut, CaseAttemptStatus.Error, CaseAttemptStatus.Blocked],
            ignoreOrder: false);
    }
}
