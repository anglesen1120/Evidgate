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
/// The wall-clock budget of one extraction plus the caller's token. The parser profile and PdfPig do not know the
/// budget, so the readers call <see cref="ThrowIfExpired"/> between pages, parts and XML nodes, and
/// <see cref="Token"/> (caller cancellation linked with the budget) goes to every profile call so a long call stops at the deadline.
/// </summary>
internal sealed class ExtractionDeadline : IDisposable
{
    private readonly TimeProvider time;
    private readonly long started;
    private readonly TimeSpan limit;
    private readonly CancellationToken callerToken;
    private readonly CancellationTokenSource budget;
    private readonly CancellationTokenSource linked;

    /// <summary>Starts the clock.</summary>
    /// <param name="limit">The budget; a budget of zero is expired at the first check.</param>
    /// <param name="cancellationToken">The caller's token; cancellation is never turned into a refusal.</param>
    /// <param name="timeProvider">The clock; the system clock when null.</param>
    public ExtractionDeadline(TimeSpan limit, CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        time = timeProvider ?? TimeProvider.System;
        started = time.GetTimestamp();
        this.limit = limit;
        callerToken = cancellationToken;
        budget = new CancellationTokenSource(limit, time);
        linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
    }

    /// <summary>The caller's cancellation linked with the budget, for the parser profile calls that take a token.</summary>
    public CancellationToken Token => linked.Token;

    /// <summary>True when the budget, not the caller, cancelled <see cref="Token"/>.</summary>
    public bool BudgetCancelled => linked.IsCancellationRequested && !callerToken.IsCancellationRequested;

    /// <summary>Stops with the caller's cancellation, or with <c>extraction_timeout</c> once the budget is spent.</summary>
    public void ThrowIfExpired()
    {
        callerToken.ThrowIfCancellationRequested();

        if (time.GetElapsedTime(started) >= limit)
        {
            throw new ExtractionRefusedException(IntakeFailureCode.ExtractionTimeout);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        linked.Dispose();
        budget.Dispose();
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
