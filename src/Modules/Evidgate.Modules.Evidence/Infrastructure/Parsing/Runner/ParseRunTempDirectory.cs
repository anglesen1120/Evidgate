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
/// share a temp root. The mode is set by the create call itself, not after it, so the directory is never visible
/// to another account with a wider mode even for a moment.
/// </para>
/// <para>
/// The configured root is resolved to an absolute path here and not by the binder: the child receives the
/// directory as <c>TMPDIR</c> and as its working directory, so a relative root would otherwise resolve against
/// whatever directory the host happened to be started from.
/// </para>
/// </summary>
internal static class ParseRunTempDirectory
{
    private const string Prefix = "evidgate-parse-";

    /// <summary>The owner's whole access: read, write and, for a directory, search. Also what cleanup restores.</summary>
    private const UnixFileMode OwnerAccess =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Creates this run's directory and returns its absolute path.</summary>
    /// <param name="configuredRoot">The configured root, or an empty string for the OS temp directory.</param>
    /// <returns>The new directory's absolute path.</returns>
    public static string Create(string configuredRoot)
    {
        var root = string.IsNullOrWhiteSpace(configuredRoot) ? Path.GetTempPath() : configuredRoot;
        var path = Path.Combine(Path.GetFullPath(root), Prefix + Guid.NewGuid().ToString("N"));

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            // One call, so the tree is never created with the process umask's mode and narrowed afterwards: the
            // child gets a directory no other account can read from its first visible instant.
            Directory.CreateDirectory(path, OwnerAccess);
        }

        return path;
    }

    /// <summary>
    /// Removes the directory and everything in it. A removal that fails once is retried once (a just-killed child's
    /// handle can hold a directory open for a moment, and a directory the child chmod'ed to 000 cannot be walked):
    /// on Unix the owner restores its own read, write and search bits recursively before the retry, and the retry
    /// follows immediately because on Unix the open handle the first attempt lost to is closed by the kill that
    /// preceded it — no sleep, so cleanup cannot stall the run's own thread. A removal that still fails is left
    /// alone — a leftover directory under the temp root must never turn a finished parse into an exception — and
    /// reported so a test or a future metric can see it instead of only noticing the disk.
    /// </summary>
    /// <param name="path">The directory <see cref="Create"/> returned.</param>
    /// <returns>True when the directory is gone, false when something under it survived both attempts.</returns>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Cleanup runs in a finally on every path and may never replace the run's verdict; the two "
            + "exception families a recursive delete raises on Unix are caught and the second attempt is final.")]
    public static bool Delete(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return true;
                }

                Directory.Delete(path, recursive: true);

                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (!OperatingSystem.IsWindows())
                {
                    // A directory without the owner's search bit cannot be enumerated by the delete at all, so the
                    // recursive walk has to restore access first — one level at a time, from the top down. The retry
                    // needs no pause: nothing on Unix releases a handle slower than the retry loop itself.
                    RestoreOwnerAccess(path);

                    continue;
                }

                // Windows releases a directory handle asynchronously after the process that held it exits, so the
                // only way to avoid a lost attempt there is to wait the handle out.
                Thread.Sleep(100);
            }
        }

        return !Directory.Exists(path);
    }

    /// <summary>
    /// Restores the owner's read, write and search bits on <paramref name="path"/> and everything below it, one
    /// directory at a time: a directory without its search bit cannot be listed, so the walk chmods before it
    /// descends. Symbolic links are skipped, so a link the child left cannot redirect the chmod outside the tree.
    /// </summary>
    /// <param name="path">A directory, or one entry of one.</param>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A best-effort repair before the final delete attempt: an entry that still cannot be "
            + "touched is the delete retry's to report, and cleanup never replaces the run's verdict.")]
    private static void RestoreOwnerAccess(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, OwnerAccess);

            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                RestoreOwnerAccess(entry);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The entry vanished under the walk, or it is not this account's to repair: the retry decides.
        }
    }
}
