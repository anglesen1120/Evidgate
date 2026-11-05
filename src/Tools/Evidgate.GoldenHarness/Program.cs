namespace Evidgate.GoldenHarness;

/// <summary>
/// Console entry point of the golden-fixture harness. The parser table it runs with is
/// <see cref="GoldenParsers.Create"/>, so a case is replayed through the producer's real mapper; a producer
/// whose mapper has not landed stays unregistered, and its cases keep reporting <c>PENDING</c>.
/// </summary>
internal static class Program
{
    private static int Main(string[] args) => HarnessRunner.Run(args, GoldenParsers.Create(), Console.Out);
}
