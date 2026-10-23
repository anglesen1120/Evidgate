using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Hosts.IntegrationTests;

/// <summary>
/// Locates the built host binaries the real-process tests run
/// (<c>ShutdownTests</c>, <c>OptionsValidationTests</c>): the apphost in the
/// host project's output for the test's own configuration.
/// </summary>
internal static class HostLocator
{
    /// <summary>The repository root (walks up from the test output to Evidgate.slnx).</summary>
    public static string RepoRoot { get; } = LocateRepoRoot();

    /// <summary>Build configuration the tests run under (Debug or Release).</summary>
    public static string Configuration { get; } =
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name; // bin/<Debug|Release>/net10.0

    /// <summary>Path of the host's apphost executable (src/&lt;Host&gt;/bin/&lt;Config&gt;/net10.0/&lt;Host&gt;).</summary>
    public static string AppHost(string project)
    {
        var path = Path.Combine(RepoRoot, "src", project, "bin", Configuration, "net10.0", project);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"host binary not found at {path}; build src/{project} first", path);
        }

        return path;
    }

    /// <summary>A free TCP port for the process-under-test's listener.</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"repository root (Evidgate.slnx) not found above '{AppContext.BaseDirectory}'");
    }
}
