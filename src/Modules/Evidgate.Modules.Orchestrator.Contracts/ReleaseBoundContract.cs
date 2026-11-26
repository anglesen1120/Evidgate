namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>The stable name of the outbox event an applied release binding writes, shared with its consumers.</summary>
public static class ReleaseBoundContract
{
    /// <summary>The outbox task type of an applied binding: <c>orchestrator.release.bound</c>.</summary>
    public const string OutboxTaskType = "orchestrator.release.bound";
}
