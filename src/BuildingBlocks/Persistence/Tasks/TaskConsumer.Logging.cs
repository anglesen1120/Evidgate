using Microsoft.Extensions.Logging;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

public sealed partial class TaskConsumer
{
    [LoggerMessage(
        EventId = 2300,
        Level = LogLevel.Warning,
        Message = "Claim pass failed for queue {Queue}; retrying after the poll interval")]
    private partial void LogPollFailed(string queue, Exception exception);

    [LoggerMessage(
        EventId = 2312,
        Level = LogLevel.Information,
        Message = "Released {Released} of {Waiting} claim(s) whose lease expired while they waited in the batch; their attempts are given back")]
    private partial void LogReleased(int waiting, int released);

    [LoggerMessage(
        EventId = 2313,
        Level = LogLevel.Warning,
        Message = "Task {TaskId} could not be released; the reclaim path will redeliver it after its lease")]
    private partial void LogReleaseFailed(Guid taskId, Exception exception);

    [LoggerMessage(
        EventId = 2309,
        Level = LogLevel.Information,
        Message = "Shutdown: {Remaining} claimed task row(s) left for the next worker (first is {TaskId}); their leases redeliver them")]
    private partial void LogShutdownBeforeStarting(Guid taskId, int remaining);

    [LoggerMessage(
        EventId = 2308,
        Level = LogLevel.Warning,
        Message = "Task {TaskId} ({TaskType}) skipped: its lease expired while it waited in the batch, so the claimer's reclaim path owns it now")]
    private partial void LogLeaseExpiredBeforeRun(Guid taskId, string taskType);

    [LoggerMessage(
        EventId = 2301,
        Level = LogLevel.Debug,
        Message = "Claim pass still failing for queue {Queue}")]
    private partial void LogPollFailedAgain(string queue, Exception exception);

    [LoggerMessage(
        EventId = 2302,
        Level = LogLevel.Information,
        Message = "Task {TaskId} ({TaskType}) done")]
    private partial void LogDone(Guid taskId, string taskType);

    [LoggerMessage(
        EventId = 2303,
        Level = LogLevel.Warning,
        Message = "Task {TaskId} ({TaskType}) finished after its lease was lost; the newer attempt owns the row")]
    private partial void LogLeaseLost(Guid taskId, string taskType);

    [LoggerMessage(
        EventId = 2304,
        Level = LogLevel.Error,
        Message = "Task {TaskId} ran but its completion could not be written; the lease will redeliver it")]
    private partial void LogCompletionFailed(Guid taskId, Exception exception);

    [LoggerMessage(
        EventId = 2305,
        Level = LogLevel.Warning,
        Message = "Task {TaskId} ({TaskType}) attempt {Attempt} failed, retrying in {BackoffSeconds}s: {Error}")]
    private partial void LogRetry(Guid taskId, string taskType, int attempt, double backoffSeconds, string error);

    [LoggerMessage(
        EventId = 2306,
        Level = LogLevel.Error,
        Message = "Task {TaskId} ({TaskType}) dead-lettered after {Attempts} attempts: {Error}")]
    private partial void LogDeadLetter(Guid taskId, string taskType, int attempts, string error);

    [LoggerMessage(
        EventId = 2307,
        Level = LogLevel.Error,
        Message = "Task {TaskId}: the failure transition could not be written; the lease will redeliver it")]
    private partial void LogTransitionFailed(Guid taskId, Exception exception);

    [LoggerMessage(
        EventId = 2314,
        Level = LogLevel.Information,
        Message = "Shutdown: task {TaskId} ({TaskType}) was cancelled mid-handler and released; its attempt is given back")]
    private partial void LogReleasedOnShutdown(Guid taskId, string taskType);
}
