using System.Diagnostics.CodeAnalysis;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

/// <summary>
/// The per-run temp directory (06.3.6 T1.3 behaviour 8): one fresh private directory under the configured root,
/// handed to the child as both <c>TMPDIR</c> and its working directory, and removed recursively on every path —
/// success, cap breach, kill, spawn failure, exception and cancellation. The child itself is forbidden to open a
/// file (06.3.6 AC1); the directory exists so that the runtime, a future spool and the OS have somewhere private
/// to write, and so nothing the child could touch is shared between two runs.
/// <para>
/// The mode is 0700 on Unix and the name is unique, so one run cannot read another's directory even when they
/// share a temp root.
/// </para>
/// </summary>
internal static class ParseRunTempDirectory
{
    private const string Prefix = "evidgate-parse-";

    private const UnixFileMode PrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Creates this run's directory and returns its absolute path.</summary>
    /// <param name="configuredRoot">The configured root, or an empty string for the OS temp directory.</param>
    /// <returns>The new directory's absolute path.</returns>
    public static string Create(string configuredRoot)
    {
        var root = string.IsNullOrWhiteSpace(configuredRoot) ? Path.GetTempPath() : configuredRoot;
        var path = Path.Combine(root, Prefix + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(path);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateDirectory);
        }

        return path;
    }

    /// <summary>
    /// Removes the directory and everything in it. A removal that fails once is retried once (a just-killed child's
    /// handle can hold a directory open for a moment) and then left alone: a leftover directory under the temp root
    /// must never turn a finished parse into an exception.
    /// </summary>
    /// <param name="path">The directory <see cref="Create"/> returned.</param>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Cleanup runs in a finally on every path and may never replace the run's verdict; the two "
            + "exception families a recursive delete raises on Unix are caught and the second attempt is final.")]
    public static void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
