using Evidgate.Worker;

// Entry point only; the composition lives in WorkerHost so the integration
// tests run the very same services (AC5).
return await WorkerHost.RunAsync(args);

/// <summary>
/// The top-level entry point, declared so it stays internal: the ASP.NET Core
/// shared framework (reached through module references) otherwise generates a
/// public <c>Program</c> that collides with the Api's in a test assembly
/// referencing both hosts (CS0433).
/// </summary>
internal partial class Program;

namespace Evidgate.Worker
{
    /// <summary>
    /// Worker marker type for <c>WebApplicationFactory</c>-style host factories.
    /// Named WorkerProgram (not Program) so it doesn't collide with the Api's
    /// marker when a test assembly references both hosts.
    /// </summary>
    public partial class WorkerProgram
    {
    }
}
