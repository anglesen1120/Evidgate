namespace Evidgate.Evaluation;

/// <summary>One planned execution whose last considered attempt passed after an earlier attempt failed.</summary>
/// <param name="ExecutionKey">The planned execution.</param>
/// <param name="CaseId">The catalog case the execution runs.</param>
/// <param name="RetryCount">How many attempts followed the first one within the considered window.</param>
/// <param name="HadRetryFailToPass">True when an earlier attempt failed and the last one passed; always true for an emitted fact.</param>
public sealed record FlakyFact(string ExecutionKey, Guid CaseId, int RetryCount, bool HadRetryFailToPass);

/// <summary>Finds the executions that only passed on a retry.</summary>
public interface IFlakyDetector
{
    /// <summary>Detects flaky executions among the attempts, ordered by execution key (ordinal).</summary>
    /// <param name="attempts">Every admitted attempt, in any order.</param>
    /// <param name="maxRetries">
    /// The suite revision's retry limit: attempts beyond <c>maxRetries + 1</c> are not considered; null means unbounded.
    /// </param>
    IReadOnlyList<FlakyFact> Detect(IReadOnlyList<CaseAttemptInput> attempts, int? maxRetries);
}

/// <summary>
/// The default detector. Blocked and skipped attempts count on neither side: they are not failures and never a
/// recovery, so a run that ended blocked or skipped yields no fact.
/// </summary>
public sealed class FlakyDetector : IFlakyDetector
{
    /// <inheritdoc />
    public IReadOnlyList<FlakyFact> Detect(IReadOnlyList<CaseAttemptInput> attempts, int? maxRetries)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        // With no retries allowed only attempt one is considered, and one attempt cannot be a retry that recovered.
        if (maxRetries is <= 0)
        {
            return [];
        }

        // Widened so a limit of int.MaxValue cannot wrap into a negative bound.
        var lastConsidered = maxRetries is { } limit ? (long)limit + 1 : long.MaxValue;
        var byKey = new Dictionary<string, List<CaseAttemptInput>>(StringComparer.Ordinal);
        foreach (var attempt in attempts)
        {
            if (attempt.AttemptNo > lastConsidered)
            {
                continue;
            }

            if (!byKey.TryGetValue(attempt.ExecutionKey, out var group))
            {
                group = [];
                byKey.Add(attempt.ExecutionKey, group);
            }

            group.Add(attempt);
        }

        var facts = new List<FlakyFact>();
        foreach (var key in byKey.Keys.Order(StringComparer.Ordinal))
        {
            if (ToFact(byKey[key]) is { } fact)
            {
                facts.Add(fact);
            }
        }

        return facts;
    }

    private static FlakyFact? ToFact(List<CaseAttemptInput> group)
    {
        // The status breaks a tie on a duplicated attempt number, so the order never depends on the input order.
        group.Sort(static (left, right) => left.AttemptNo != right.AttemptNo
            ? left.AttemptNo.CompareTo(right.AttemptNo)
            : left.Status.CompareTo(right.Status));

        var last = group[^1];
        if (last.Status != CaseStatus.Passed)
        {
            return null;
        }

        for (var index = 0; index < group.Count - 1; index++)
        {
            if (RequiredScope.IsFailure(group[index].Status))
            {
                return new FlakyFact(last.ExecutionKey, last.CaseId, group.Count - 1, true);
            }
        }

        return null;
    }
}
