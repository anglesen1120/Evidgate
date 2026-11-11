using Xunit;

[assembly: AssemblyFixture(typeof(Evidgate.Testing.NoHealingProvider))]

namespace Evidgate.Testing;

/// <summary>
/// Selects the disabled healing provider for every suite that boots a host pinned to Production: such a host refuses to
/// start while no provider is stated, and no test reaches a real provider. A value already present in the environment
/// wins, so a run that configured one keeps it.
/// </summary>
public sealed class NoHealingProvider
{
    /// <summary>The setting whose <c>none</c> value states that healing may call no provider.</summary>
    public const string Variable = "EVIDGATE__HEALING__LLM__PROVIDER";

    /// <summary>Arms the selection for the lifetime of the test process.</summary>
    public NoHealingProvider()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Environment.SetEnvironmentVariable(Variable, "none");
        }
    }
}
