using Evidgate.Worker;

// Entry point only; the composition lives in WorkerHost so the integration
// tests run the very same services (AC5).
return await WorkerHost.RunAsync(args);

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
