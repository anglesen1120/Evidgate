namespace Evidgate.Migrator;

/// <summary>
/// Entry point: applies <c>migrations/</c> as the owner role (00 §5.2, ADR-0003).
/// A failed migration means an environment is not deployable, so a runtime
/// failure must never be converted into a stack trace and an exit code of 1.
/// </summary>
internal static class Program
{
    private static int Main(string[] args) => MigratorHost.Run(args, Console.Out, Console.Error);
}
