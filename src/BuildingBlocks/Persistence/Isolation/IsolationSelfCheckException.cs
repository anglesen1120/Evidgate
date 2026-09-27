namespace Evidgate.BuildingBlocks.Persistence.Isolation;

/// <summary>
/// Thrown by <see cref="IsolationSelfCheckHostedService"/> when the boot-time
/// isolation self-check of 02.6.1-T1.4 (AC5) refuses the start: a table the
/// exception list does not excuse is not under <c>ENABLE</c> + <c>FORCE ROW LEVEL
/// SECURITY</c> with the policy shape its kind requires, or the catalogue could not
/// be read at all. Carrying a named code and a bounded list of table names, never a
/// connection string or any other secret (00 §5.7 S3).
/// </summary>
public sealed class IsolationSelfCheckException : InvalidOperationException
{
    /// <summary>The named error every refusal speaks (the boot line, the exit code and the tests).</summary>
    public const string Code = IsolationSelfCheckCodes.SelfCheckFailed;

    /// <summary>How many violations the message lists before it summarises the rest.</summary>
    private const int MaxListedViolations = 20;

    /// <summary>Creates the refusal from the report that failed.</summary>
    public IsolationSelfCheckException(IsolationSelfCheckReport report)
        : base(BuildMessage(report))
    {
        Report = report;
    }

    /// <summary>The report that failed, so a caller can log or assert on the detail.</summary>
    public IsolationSelfCheckReport Report { get; }

    /// <summary>
    /// The refusal inside <paramref name="exception"/>, if any. The host wraps a
    /// failing <c>IHostedService.StartAsync</c> in an <c>AggregateException</c>, so a
    /// boot path that wants one clean line looks through the wrappers instead of
    /// pattern-matching on the outer type.
    /// </summary>
    public static IsolationSelfCheckException? FindIn(Exception? exception) => exception switch
    {
        null => null,
        IsolationSelfCheckException refusal => refusal,
        AggregateException aggregate => aggregate.InnerExceptions
            .Select(FindIn)
            .FirstOrDefault(found => found is not null),
        _ => FindIn(exception.InnerException),
    };

    private static string BuildMessage(IsolationSelfCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        // "Cannot confirm" is a refusal too: the check reports an unreadable catalogue
        // rather than a violation list, and both must stop the boot.
        if (report.FailureCode is not null)
        {
            return $"{Code}: {report.FailureCode} — {report.FailureDetail}";
        }

        var listed = string.Join(
            "; ",
            report.Violations.Take(MaxListedViolations).Select(violation => violation.ToString()));
        var more = report.Violations.Count > MaxListedViolations
            ? $" (+{report.Violations.Count - MaxListedViolations} more)"
            : string.Empty;

        return $"{Code}: {report.Violations.Count} rule violation(s) across "
            + $"{report.CheckedTables.Count} checked table(s): {listed}{more}";
    }
}
