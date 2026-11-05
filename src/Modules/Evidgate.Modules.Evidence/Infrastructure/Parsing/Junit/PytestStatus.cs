namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;

/// <summary>
/// The JUnit status vocabulary one producer dialect uses. The raw word is the element the report carried
/// (<c>failure</c>, <c>error</c>, <c>skipped</c>, or the implicit <c>passed</c>), and the normalised value is
/// what the event stores; keeping both means a reader can always see the producer's own spelling.
/// <para>
/// Where one testcase carries more than one outcome element (a retried or doubly reported case), the more severe
/// one wins — <c>error</c>, then <c>failure</c>, then <c>skipped</c> — so the choice is deterministic and never
/// understates a failure.
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
}
