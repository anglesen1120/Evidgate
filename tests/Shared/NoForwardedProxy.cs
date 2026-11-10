using Xunit;

[assembly: AssemblyFixture(typeof(Evidgate.Testing.NoForwardedProxy))]

namespace Evidgate.Testing;

/// <summary>
/// Declares the Api's proxy stance for every suite that boots it pinned to Production: those hosts refuse to
/// start while no proxy is named, and no test proxies a request. A value already present in the environment
/// wins, so a run configured against a real proxy keeps it.
/// </summary>
public sealed class NoForwardedProxy
{
    /// <summary>The setting whose <c>none</c> value states that no proxy sits in front of the Api.</summary>
    public const string Variable = "EVIDGATE__FORWARDEDHEADERS__KNOWNPROXIES";

    /// <summary>Arms the stance for the lifetime of the test process.</summary>
    public NoForwardedProxy()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Environment.SetEnvironmentVariable(Variable, "none");
        }
    }
}
