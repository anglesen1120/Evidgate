namespace Evaluation.UnitTests;

using Evidgate.Evaluation;

/// <summary>The fixed inputs the evaluator tests share: deterministic ids, one required case, no I/O anywhere.</summary>
internal static class Scenarios
{
    public static readonly Guid CheckoutCaseId = Guid.Parse("0190f0a0-0000-7000-8000-000000000001");

    public static readonly Guid SearchCaseId = Guid.Parse("0190f0a0-0000-7000-8000-000000000002");

    public const string CheckoutKey = "checkout/card";

    public const string SearchKey = "search/query";

    public const string SmokeSuite = "smoke";

    public const string TraceKind = "trace";

    public static PolicyRevisionInput Policy(
        IReadOnlyList<string>? requiredCaseKeys = null,
        IReadOnlyList<string>? requiredSuiteKeys = null,
        IReadOnlyList<Guid>? criticalCaseIds = null,
        IReadOnlyList<string>? allowedSkipCaseKeys = null,
        IReadOnlyList<string>? requiredArtifactKinds = null,
        bool emptySuiteAllowed = false,
        int flakyBudget = 1) => new(
            requiredCaseKeys ?? [CheckoutKey],
            requiredSuiteKeys ?? [],
            criticalCaseIds ?? [],
            allowedSkipCaseKeys ?? [],
            flakyBudget,
            requiredArtifactKinds ?? [TraceKind],
            emptySuiteAllowed);

    public static EvidenceRevisionInput Evidence(
        IReadOnlyList<CaseResultInput>? results = null,
        IReadOnlyList<MissingReasonCode>? missing = null,
        int expectedExecutionCount = 1,
        bool suiteDeclaredEmpty = false) => new(
            expectedExecutionCount,
            suiteDeclaredEmpty,
            missing ?? [],
            results ?? [Result(CaseStatus.Passed)]);

    public static CaseResultInput Result(
        CaseStatus status,
        Guid? caseId = null,
        string? caseKey = null,
        string suiteKey = SmokeSuite,
        bool required = true) => new(
            caseId ?? CheckoutCaseId,
            caseKey ?? CheckoutKey,
            suiteKey,
            status,
            required);

    public static ExceptionsSnapshotInput NoExceptions => new(DateTimeOffset.UnixEpoch, [], []);
}
