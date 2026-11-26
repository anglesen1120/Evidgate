using System.Diagnostics;
using System.Text;

namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>
/// Raised inside the readers to stop at the first refusal. It carries only a closed failure code, never a name or
/// byte of the document, and never leaves <see cref="DocumentTextExtractor"/>.
/// </summary>
internal sealed class ExtractionRefusedException(string failureCode) : Exception(failureCode)
{
    /// <summary>The closed failure code to report.</summary>
    public string FailureCode { get; } = failureCode;
}

/// <summary>
/// The wall-clock budget of one extraction plus the caller's token. The parser profile and PdfPig both ignore
/// the budget, so the readers call <see cref="ThrowIfExpired"/> between pages, parts and XML nodes.
/// </summary>
internal sealed class ExtractionDeadline
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly TimeSpan limit;
    private readonly CancellationToken cancellationToken;

    /// <summary>Starts the clock.</summary>
    /// <param name="limit">The budget; a budget of zero is expired at the first check.</param>
    /// <param name="cancellationToken">The caller's token; cancellation is never turned into a refusal.</param>
    public ExtractionDeadline(TimeSpan limit, CancellationToken cancellationToken)
    {
        this.limit = limit;
        this.cancellationToken = cancellationToken;
    }

    /// <summary>The caller's token, for the parser profile calls that take one.</summary>
    public CancellationToken Token => cancellationToken;

    /// <summary>Stops with the caller's cancellation, or with <c>extraction_timeout</c> once the budget is spent.</summary>
    public void ThrowIfExpired()
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (clock.Elapsed >= limit)
        {
            throw new ExtractionRefusedException(IntakeFailureCode.ExtractionTimeout);
        }
    }
}

/// <summary>
/// Collects extracted text and refuses the moment it would hold more than the cap, so the buffer never grows past
/// cap plus one append: the overflow is an error, never a silent truncation.
/// </summary>
internal sealed class TextAccumulator(int maxChars)
{
    private readonly StringBuilder text = new();
    private bool breakPending;

    /// <summary>The characters collected so far, not counting a pending line break.</summary>
    public int Length => text.Length;

    /// <summary>Appends one character.</summary>
    /// <param name="value">The character.</param>
    public void Append(char value)
    {
        Reserve(1);
        text.Append(value);
    }

    /// <summary>Appends a run of characters.</summary>
    /// <param name="value">The characters.</param>
    public void Append(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        Reserve(value.Length);
        text.Append(value);
    }

    /// <summary>
    /// Asks for a line break before the next text. It is lazy, so a break after the last text never counts against
    /// the cap and never leaves a trailing newline.
    /// </summary>
    public void BreakLine() => breakPending = text.Length > 0;

    /// <summary>The collected text.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => text.ToString();

    private void Reserve(int count)
    {
        var needed = breakPending ? count + 1 : count;

        if (needed > maxChars - text.Length)
        {
            throw new ExtractionRefusedException(IntakeFailureCode.CharLimitExceeded);
        }

        if (breakPending)
        {
            text.Append('\n');
            breakPending = false;
        }
    }
}
