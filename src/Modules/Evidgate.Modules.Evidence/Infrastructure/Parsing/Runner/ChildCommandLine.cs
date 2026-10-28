using System.Diagnostics;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

/// <summary>
/// The process one run starts: the executable and the arguments that name the child, before the per-run vector is
/// appended. Production is <c>dotnet &lt;absolute child dll&gt;</c> (06.3.6 §7): the runtime image is chiseled — no
/// shell, no <c>setsid</c>, no util-linux — and its entrypoint is <c>/usr/bin/dotnet</c>, so the child is always a
/// managed assembly started by the shared host. Tests substitute a scripted child through the same seam, which is
/// why the shape is a value and not a method on the runner.
/// </summary>
/// <param name="Executable">The program to start: the dotnet host, or a test child such as <c>/bin/sh</c>.</param>
/// <param name="LeadingArguments">
/// The arguments before the profile vector: the child assembly for production, the script arguments for a test child.
/// </param>
/// <param name="ChildAssemblyPath">
/// The child assembly the run starts, used to find the runtimeconfig that decides whether the globalization
/// environment variable is needed; an empty string for a command that is not a managed assembly.
/// </param>
internal readonly record struct ChildCommand(
    string Executable,
    IReadOnlyList<string> LeadingArguments,
    string ChildAssemblyPath);

/// <summary>
/// Builds the child's command line without a shell and without string concatenation (06.3.6 T1.3 behaviour 1):
/// every argument is its own <see cref="ProcessStartInfo.ArgumentList"/> entry, so a media type or a path with a
/// space, a quote or a newline cannot become a second argument.
/// </summary>
internal static class ChildCommandLine
{
    /// <summary>
    /// The production command: the dotnet host and the child assembly resolved from the options
    /// (<c>EVIDGATE__PARSE__CHILDPATH</c>, relative to the host's base directory in the worker image).
    /// </summary>
    /// <param name="options">The runner's options.</param>
    /// <returns>The executable plus the absolute child assembly path.</returns>
    public static ChildCommand ForOptions(ParseRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var childAssembly = ResolveChildAssembly(options.ChildPath, AppContext.BaseDirectory);

        return new ChildCommand(
            ResolveDotnetHost(Environment.ProcessPath),
            [childAssembly],
            childAssembly);
    }

    /// <summary>
    /// The dotnet host to start the child with. <c>DOTNET_HOST_PATH</c> is deliberately not read: it is a build
    /// convenience no deployment sets, and an inherited environment must not decide what the parse child runs. The
    /// host process itself (when it is <c>dotnet</c>, which is the chiseled image's entrypoint) is the exact binary
    /// that is already running; anything else falls back to <c>dotnet</c> on <c>PATH</c>.
    /// </summary>
    /// <param name="processPath">The current process's path, normally <see cref="Environment.ProcessPath"/>.</param>
    /// <returns>The absolute host path, or <c>dotnet</c> for <see cref="Process"/> to resolve.</returns>
    public static string ResolveDotnetHost(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return "dotnet";
        }

        return string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.Ordinal)
            ? processPath
            : "dotnet";
    }

    /// <summary>
    /// Resolves the child assembly: a relative configured path is joined to the host's base directory (the image
    /// publishes the child next to the worker's own output), an absolute one is kept.
    /// </summary>
    /// <param name="childPath">The configured path, e.g. <c>parser/Evidgate.Parser.dll</c>.</param>
    /// <param name="baseDirectory">The directory a relative path resolves against.</param>
    /// <returns>The absolute child assembly path.</returns>
    public static string ResolveChildAssembly(string childPath, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(childPath);
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);

        return Path.IsPathRooted(childPath)
            ? Path.GetFullPath(childPath)
            : Path.GetFullPath(Path.Combine(baseDirectory, childPath));
    }
}
