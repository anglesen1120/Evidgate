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

    /// <summary>The network list; when a run sets it the direct default must not be added next to it.</summary>
    public const string NetworksVariable = "EVIDGATE__FORWARDEDHEADERS__KNOWNNETWORKS";

    /// <summary>Arms the stance for the lifetime of the test process.</summary>
    public NoForwardedProxy()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(NetworksVariable)))
        {
            Environment.SetEnvironmentVariable(Variable, "none");
        }
    }
}
