using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Authoring.Infrastructure.Intake.Jobs;

/// <summary>The <c>Authoring:Generation:Intake:Execution</c> section: what one Worker process may run at once.</summary>
internal sealed class IntakeExecutionOptions
{
    /// <summary>The configuration path of the section.</summary>
    internal const string SectionName = "Authoring:Generation:Intake:Execution";

    /// <summary>The largest value a host may state.</summary>
    internal const int Ceiling = 64;

    /// <summary>Document parsers one Worker process may have running, abandoned ones included.</summary>
    public int MaxConcurrentParsers { get; set; } = 4;

    /// <summary>True when the member is inside its range.</summary>
    internal static bool IsValid(IntakeExecutionOptions options) => options.MaxConcurrentParsers is >= 1 and <= Ceiling;
}

/// <summary>
/// The Worker-wide bound on running document parsers. The per-tenant slot bounds one tenant; this bounds the process,
/// so many tenants at their own limit cannot together run more parsers than the host has threads and memory for.
/// A permit is taken before an attempt starts and is given back by the parser itself when it exits, not when the attempt
/// stops waiting for it: a parser abandoned at its deadline cannot be killed and keeps running, so it keeps its permit.
/// </summary>
internal sealed class IntakeExecutionPermits(IOptions<IntakeExecutionOptions> options)
{
    private readonly int _capacity = options.Value.MaxConcurrentParsers;
    private int _held;

    /// <summary>Takes one permit without waiting; false when every permit is held.</summary>
    internal bool TryEnter()
    {
        if (Interlocked.Increment(ref _held) <= _capacity)
        {
            return true;
        }

        Interlocked.Decrement(ref _held);

        return false;
    }

    /// <summary>Gives one permit back.</summary>
    internal void Exit() => Interlocked.Decrement(ref _held);
}
