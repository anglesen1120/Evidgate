using System.Runtime.InteropServices;

namespace Evidgate.Parser;

/// <summary>
/// The child's own OS limits (06.3.6 §7, hand-over Notes <c>Self-applied limits</c>): when the launcher asks for
/// them, the child applies <c>RLIMIT_CPU</c>, <c>RLIMIT_FSIZE</c> and <c>RLIMIT_CORE = 0</c> to itself through
/// <c>setrlimit(2)</c> before it reads a byte of stdin.
/// <para>
/// Self-applied rather than launcher-applied because the worker image is chiselled: it carries no shell, no
/// <c>setsid</c> and no util-linux <c>prlimit</c>, and the only entry point is <c>/usr/bin/dotnet</c>. The child
/// can therefore not be wrapped in a launcher that sets the limits before <c>exec</c>, and it owns no process but
/// itself, so it sets them on the process it is (AC2). This is the one native import in the assembly
/// (<c>Architecture.Tests</c> allows exactly this declaration and denies every other P/Invoke in the child and in
/// the profile it links); <c>RLIMIT_NPROC</c> is deliberately absent (shared uid, DECISIONS F14) and so is
/// <c>RLIMIT_AS</c> (the .NET runtime cannot start under a coarse address-space cap; memory is bounded by
/// <c>DOTNET_GCHeapHardLimit</c> and the cgroup instead).
/// </para>
/// <para>
/// <c>rlim_t</c> is 64-bit on Linux and on Darwin, and the three resource numbers are the same on both
/// (<c>&lt;sys/resource.h&gt;</c>: CPU = 0, FSIZE = 1, CORE = 4), so one declaration covers the two hosts the
/// child runs on. Soft and hard are always set to the same value: the child has no reason to keep headroom it
/// could raise into, and a hard limit equal to the soft one means a surprise <c>setrlimit</c> "fix" cannot lift
/// the ceiling the launcher measured. A requested limit that cannot be applied is not a degraded run but a
/// refusal — the caller writes one fixed word and exits 3 before stdin is read (fail closed, no marker).
/// </para>
/// </summary>
internal static class ResourceLimits
{
    /// <summary><c>RLIMIT_CPU</c>: CPU seconds (Linux and Darwin: 0).</summary>
    private const int CpuResource = 0;

    /// <summary><c>RLIMIT_FSIZE</c>: the largest file the process may create (Linux and Darwin: 1).</summary>
    private const int FileSizeResource = 1;

    /// <summary><c>RLIMIT_CORE</c>: the largest core dump (Linux and Darwin: 4).</summary>
    private const int CoreResource = 4;

    /// <summary>
    /// <c>int setrlimit(int resource, const struct rlimit *limit)</c> from the platform's C library. The one
    /// native import the child declares; <see cref="ResourceLimit"/> is the kernel's two <c>rlim_t</c> fields, so
    /// no marshalling beyond the struct reference happens.
    /// </summary>
    /// <param name="resource">The <c>RLIMIT_*</c> number.</param>
    /// <param name="limit">The soft and hard values, both applied.</param>
    /// <returns>0 on success, -1 with <c>errno</c> set on failure (never logged: the child says one fixed word).</returns>
    [DllImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
    private static extern int SetRlimit(int resource, ref ResourceLimit limit);

    /// <summary>The kernel's <c>struct rlimit</c>: two <c>rlim_t</c> (64-bit on every host the child runs on).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ResourceLimit
    {
        /// <summary>The value the kernel enforces.</summary>
        public ulong Soft;

        /// <summary>The ceiling only a privileged process could raise the soft value to.</summary>
        public ulong Hard;
    }

    /// <summary>
    /// Applies the limits the launcher asked for, before stdin is read.
    /// <para>
    /// With neither <c>--cpu-seconds</c> nor <c>--max-file-bytes</c> nothing is applied and the child inherits the
    /// launcher's limits, which is what the in-process tests and a developer run want. A host the child is never
    /// shipped to (Windows) cannot honour a request at all, so a request there is a refusal rather than a silent
    /// no-op: the limit is part of the run's contract, not an optional extra.
    /// </para>
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <returns>True when every requested limit was applied (or none was requested).</returns>
    public static bool TryApply(ChildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.CpuSeconds is null && options.MaxFileBytes is null)
        {
            return true;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            // Core first: from here on a fault must not write a dump of the artifact the child is about to read.
            return Apply(CoreResource, 0)
                && (options.CpuSeconds is not { } cpuSeconds || Apply(CpuResource, (ulong)cpuSeconds))
                && (options.MaxFileBytes is not { } maxFileBytes || Apply(FileSizeResource, (ulong)maxFileBytes));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // A C library without setrlimit (or without the symbol) cannot honour the contract: fail closed.
            return false;
        }
    }

    private static bool Apply(int resource, ulong value)
    {
        var limit = new ResourceLimit { Soft = value, Hard = value };

        return SetRlimit(resource, ref limit) == 0;
    }
}
