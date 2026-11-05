namespace Evidgate.GoldenHarness;

/// <summary>
/// Console entry point of the golden-fixture harness. It registers no parser: the seeded
/// corpus is marked pending until each producer's mapper lands and plugs its in-process
/// entry point into <see cref="IParserRegistry"/>.
/// </summary>
internal static class Program
{
    private static int Main(string[] args) => HarnessRunner.Run(args, new ParserRegistry(), Console.Out);
}
