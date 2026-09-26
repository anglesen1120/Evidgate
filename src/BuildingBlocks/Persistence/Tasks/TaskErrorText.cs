namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// What goes into <c>task.last_error</c> when a handler fails (01.5.1 §8 S3): a
/// redacted category and the message head, never a stack trace and never the whole
/// message. A task row is small and long-lived, and the consumer's log line is the
/// place for detail — the row only has to be enough for an operator to see which
/// class of failure poisoned the task.
/// </summary>
internal static class TaskErrorText
{
    /// <summary>Matches the <c>task_last_error_length_check</c> of 0008.</summary>
    internal const int MaxLength = 1000;

    /// <summary>Only the first line of the message survives; the type name carries the category.</summary>
    internal static string Redact(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = exception.Message;
        var lineBreak = message.AsSpan().IndexOfAny('\r', '\n');
        var head = lineBreak < 0 ? message : message[..lineBreak];
        var text = head.Length == 0 ? exception.GetType().Name : $"{exception.GetType().Name}: {head}";

        return text.Length <= MaxLength ? text : text[..MaxLength];
    }
}
