using Xunit;

namespace Hosts.IntegrationTests;

public sealed partial class OptionsValidationTests
{
    private const string SweepCaseKey = "EVIDGATE__SWEEPPROBE__CASESET";
    private const string SweepInheritedKey = "EVIDGATE__SWEEPPROBE__INHERITED";

    /// <summary>
    /// A key the case states reaches the child with the case value even when the parent exports the same key
    /// with another value, and an inherited key the case does not state is still removed.
    /// </summary>
    [Fact]
    public async Task Case_Environment_Wins_Over_Inherited_Key_And_Unstated_Inherited_Keys_Are_Removed()
    {
        // `env` prints the child's environment and exits, so the helper's own sweep is what is observed.
        var priorCase = Environment.GetEnvironmentVariable(SweepCaseKey);
        var priorInherited = Environment.GetEnvironmentVariable(SweepInheritedKey);
        Environment.SetEnvironmentVariable(SweepCaseKey, "from-parent");
        Environment.SetEnvironmentVariable(SweepInheritedKey, "from-parent");
        try
        {
            var result = await RunUntilExitAsync(
                "/usr/bin/env",
                new Dictionary<string, string> { [SweepCaseKey] = "from-case" });

            var lines = result.Stdout.Split('\n');
            Assert.Equal(0, result.ExitCode);
            Assert.Contains($"{SweepCaseKey}=from-case", lines);
            Assert.DoesNotContain($"{SweepCaseKey}=from-parent", lines);
            Assert.DoesNotContain(lines, line => line.StartsWith(SweepInheritedKey + "=", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SweepCaseKey, priorCase);
            Environment.SetEnvironmentVariable(SweepInheritedKey, priorInherited);
        }
    }
}
