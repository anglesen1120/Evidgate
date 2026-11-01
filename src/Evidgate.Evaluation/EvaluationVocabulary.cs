namespace Evidgate.Evaluation;

/// <summary>Whether a frozen evidence revision carries everything the pinned policy requires.</summary>
public enum EvaluationCompleteness
{
    /// <summary>Every required execution has a result and no missing code applies.</summary>
    Complete = 0,

    /// <summary>At least one required result is absent or invalid, so the evidence cannot decide alone.</summary>
    Incomplete = 1,
}

/// <summary>What the required scope's results say, independently of the completeness of the evidence.</summary>
public enum EvaluationOutcome
{
    /// <summary>Every execution in the required scope passed (a skip or a block is not a failure).</summary>
    AllPassed = 0,

    /// <summary>At least one required assertion failed, or a setup failed and blocked its dependants.</summary>
    HasFailures = 1,

    /// <summary>No execution in the required scope reported a result.</summary>
    NoResults = 2,
}

/// <summary>The release decision of one evidence revision against one policy revision.</summary>
public enum EvaluationDecision
{
    /// <summary>Every required case passed and the evidence is complete; never produced from missing evidence.</summary>
    Pass = 0,

    /// <summary>A required assertion failed; kept even when other evidence is missing.</summary>
    Fail = 1,

    /// <summary>The evidence cannot confirm a pass or a failure.</summary>
    Inconclusive = 2,
}

/// <summary>The closed <c>missing[]</c> vocabulary of an evidence revision; a ninth code is a contract change.</summary>
public enum MissingReasonCode
{
    /// <summary>A planned shard whose producer slot never finalized.</summary>
    MissingShard = 0,

    /// <summary>A closed shard's acknowledged events disagree with its declared count or sequence range.</summary>
    CountMismatch = 1,

    /// <summary>A required artifact is not <c>ready</c>.</summary>
    ArtifactMissing = 2,

    /// <summary>The evidence was collected from a build other than the pinned one.</summary>
    BuildMismatch = 3,

    /// <summary>A producer reported a status outside the normalized vocabulary, or reported none.</summary>
    UnknownStatus = 4,

    /// <summary>A setup failed and its dependants were blocked.</summary>
    SetupError = 5,

    /// <summary>Only events from a superseded lease epoch were received.</summary>
    StaleEpoch = 6,

    /// <summary>A planned execution recorded no terminal attempt.</summary>
    MissingResult = 7,
}

/// <summary>A normalized terminal status of one case attempt.</summary>
public enum CaseStatus
{
    /// <summary>The assertion passed.</summary>
    Passed = 0,

    /// <summary>The assertion failed.</summary>
    Failed = 1,

    /// <summary>The case was skipped.</summary>
    Skipped = 2,

    /// <summary>The case exceeded its timeout.</summary>
    TimedOut = 3,

    /// <summary>The runner reported an error.</summary>
    Error = 4,

    /// <summary>A failed setup blocked the case.</summary>
    Blocked = 5,
}

/// <summary>
/// The closed <c>reasons[]</c> catalogue of an evaluation: codes only, never report text, artifact content or a
/// producer value. The first fourteen members are emitted by the core computation; the last is reserved for the
/// flaky layer the contract names, which may only downgrade a pass.
/// </summary>
public enum EvaluationReason
{
    /// <summary>The pinned scope, build and policy passed: the only reason a core pass carries.</summary>
    ScopeBuildPolicyPinned = 0,

    /// <summary>A required assertion failed.</summary>
    RequiredAssertionFailed = 1,

    /// <summary>The evidence misses a planned shard.</summary>
    MissingShard = 2,

    /// <summary>A shard's declared and acknowledged event counts disagree.</summary>
    CountMismatch = 3,

    /// <summary>A required artifact is missing or rejected.</summary>
    ArtifactMissing = 4,

    /// <summary>The evidence belongs to a build other than the pinned one.</summary>
    BuildMismatch = 5,

    /// <summary>A producer status is unknown or was never reported.</summary>
    UnknownStatus = 6,

    /// <summary>A setup failed and blocked its dependants.</summary>
    SetupError = 7,

    /// <summary>Only stale-epoch events were received.</summary>
    StaleEpochOnly = 8,

    /// <summary>A planned execution recorded no result.</summary>
    MissingResult = 9,

    /// <summary>The required scope expects no executions and the policy does not allow an empty suite.</summary>
    ExpectedCountZero = 10,

    /// <summary>The suite is declared empty and the policy allows it, so an empty run may pass.</summary>
    EmptySuiteAllowed = 11,

    /// <summary>A case the policy requires was skipped or blocked.</summary>
    CriticalNotExecuted = 12,

    /// <summary>The policy requires an artifact kind outside the closed kind list: a policy defect.</summary>
    UnknownRequiredArtifactKind = 13,

    /// <summary>Reserved for the flaky layer: a non-critical retry flip exceeded the policy's budget.</summary>
    FlakyBudgetExceeded = 14,
}
