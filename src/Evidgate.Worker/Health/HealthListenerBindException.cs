namespace Evidgate.Worker.Health;

/// <summary>
/// Thrown from <see cref="HealthProbeListener.StartAsync"/> when the readiness
/// listener cannot bind its port (D5): the Worker must not report success when
/// its only health surface never came up, so the failure is a start failure and
/// <c>WorkerHost.RunAsync</c> turns it into a non-zero exit code.
/// </summary>
internal sealed class HealthListenerBindException : Exception
{
    /// <summary>Creates the failure for a port the process could not bind.</summary>
    public HealthListenerBindException(int port, Exception innerException)
        : base($"the readiness listener cannot bind port {port}", innerException)
        => Port = port;

    /// <summary>The port the listener could not bind.</summary>
    public int Port { get; }
}
