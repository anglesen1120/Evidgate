namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;

/// <summary>
/// The JUnit status vocabulary one producer dialect uses. The raw word is the element the report carried
/// (<c>failure</c>, <c>error</c>, <c>skipped</c>, or the implicit <c>passed</c>), and the normalised value is
/// what the event stores; keeping both means a reader can always see the producer's own spelling.
/// <para>
/// Where one planned execution carries more than one result — several outcome elements on one testcase, or a
/// testcase the report repeats (a retry) — the most severe result wins: <c>error</c>, then <c>failed</c>, then
/// <c>skipped</c>, then <c>passed</c>. The choice is deterministic and never understates a failure, and it is the
/// same order whichever result the document lists first.
/// </para>
/// </summary>
internal static class PytestStatus
{
    /// <summary>The element pytest writes for a failed assertion.</summary>
    public const string Failure = "failure";

    /// <summary>The element pytest writes for an unexpected exception.</summary>
    public const string Error = "error";

    /// <summary>The element pytest writes for a skipped test.</summary>
    public const string Skipped = "skipped";

    /// <summary>The raw word of a testcase that carries no outcome element.</summary>
    public const string Passed = "passed";

    /// <summary>The normalised value of a failed assertion.</summary>
    public const string Failed = "failed";

    /// <summary>Pairs the producer's raw word with the stored normalised value.</summary>
    /// <param name="sawFailure">The testcase carried a <c>failure</c> child.</param>
    /// <param name="sawError">The testcase carried an <c>error</c> child.</param>
    /// <param name="sawSkipped">The testcase carried a <c>skipped</c> child.</param>
    /// <returns>The raw status word and the normalised status to store.</returns>
    public static (string Raw, string Normalized) Select(bool sawFailure, bool sawError, bool sawSkipped)
    {
        if (sawError)
        {
            return (Error, Error);
        }

        if (sawFailure)
        {
            return (Failure, Failed);
        }

        return sawSkipped ? (Skipped, Skipped) : (Passed, Passed);
    }

    /// <summary>The severity order of the stored status values: <c>error</c> &gt; <c>failed</c> &gt; <c>skipped</c> &gt; <c>passed</c>.</summary>
    /// <param name="normalized">A normalised status value.</param>
    /// <returns>The severity rank; an unknown value ranks with <c>passed</c>, so it never outranks a reported result.</returns>
    public static int Severity(string normalized) => normalized switch
    {
        Error => 3,
        Failed => 2,
        Skipped => 1,
        _ => 0,
    };

    /// <summary>True when <paramref name="normalized"/> reports a more severe result than <paramref name="than"/>.</summary>
    /// <param name="normalized">The candidate status to store for the execution.</param>
    /// <param name="than">The status already kept for the same execution.</param>
    /// <returns>True when the candidate must replace the kept status.</returns>
    public static bool IsMoreSevere(string normalized, string than) =>
        Severity(normalized) > Severity(than);
}
