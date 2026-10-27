using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

/// <summary>
/// The child's environment (06.3.6 T1.3, plan Notes S117 <em>Environment</em>): a scrubbed allow-list, never an
/// inherited copy. The runtime reads <c>DOTNET_*</c> variables itself — <c>DOTNET_STARTUP_HOOKS</c>, the
/// <c>DOTNET_gc*</c> family, <c>COMPlus_*</c> — so an inherited environment would let the host (or anything that
/// could set one) change what the parse child executes or how it allocates. The runner clears
/// <see cref="System.Diagnostics.ProcessStartInfo.Environment"/> and adds only:
/// <list type="bullet">
/// <item><c>DOTNET_GCHeapHardLimit</c> — the heap ceiling the options state, hexadecimal as the runtime parses
/// it (AC2);</item>
/// <item><c>TMPDIR</c> — this run's private temp directory;</item>
/// <item><c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1</c> — only when the child's runtimeconfig does not already
/// carry <c>System.Globalization.Invariant</c> (<c>Evidgate.Parser</c> is in the Worker group of
/// <c>Directory.Build.props</c>, so its generated runtimeconfig does; a fake child in a test carries none).</item>
/// </list>
/// <c>HOME</c>, <c>PATH</c> and everything else stay unset.
/// </summary>
internal static class ChildEnvironment
{
    /// <summary>The heap ceiling the runtime reads before managed code starts.</summary>
    internal const string HeapLimitVariable = "DOTNET_GCHeapHardLimit";

    /// <summary>The temp directory the run's per-run directory is handed to the child as.</summary>
    internal const string TempDirectoryVariable = "TMPDIR";

    /// <summary>The globalization switch, set only when the child's runtimeconfig cannot declare it.</summary>
    internal const string InvariantGlobalizationVariable = "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT";

    /// <summary>The runtimeconfig property that makes <see cref="InvariantGlobalizationVariable"/> redundant.</summary>
    private const string InvariantGlobalizationProperty = "System.Globalization.Invariant";

    /// <summary>One answer per runtimeconfig path: a deployed child's file does not change under a running host.</summary>
    private static readonly ConcurrentDictionary<string, bool> InvariantByRuntimeConfig =
        new(StringComparer.Ordinal);

    /// <summary>Builds the child's whole environment for one run.</summary>
    /// <param name="childAssemblyPath">The child assembly the run starts, or an empty string for a non-dotnet command.</param>
    /// <param name="memoryLimitBytes">The heap ceiling; rendered as the runtime's hexadecimal form.</param>
    /// <param name="tempDirectory">This run's private temp directory, also the child's working directory.</param>
    /// <returns>Exactly the variables the child gets; the caller applies them to a cleared environment.</returns>
    public static Dictionary<string, string> Build(
        string childAssemblyPath,
        long memoryLimitBytes,
        string tempDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(tempDirectory);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HeapLimitVariable] = HeapLimit(memoryLimitBytes),
            [TempDirectoryVariable] = tempDirectory,
        };

        if (!DeclaresInvariantGlobalization(childAssemblyPath))
        {
            environment[InvariantGlobalizationVariable] = "1";
        }

        return environment;
    }

    /// <summary>The runtime's hexadecimal byte form of a heap ceiling, e.g. <c>0x20000000</c> for 512 MiB.</summary>
    /// <param name="bytes">The ceiling in bytes (positive).</param>
    /// <returns>The value with the <c>0x</c> prefix the runtime accepts.</returns>
    public static string HeapLimit(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        return "0x" + bytes.ToString("X", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whether the child's own runtimeconfig already declares invariant globalization, which would make the
    /// environment variable redundant. A missing or unreadable file answers <see langword="false"/>: the variable is
    /// then set, which is the safe direction for a child that must not localize.
    /// </summary>
    /// <param name="childAssemblyPath">The child assembly, or an empty string for a non-dotnet command.</param>
    /// <returns>True only when the file exists and carries the property as <see langword="true"/>.</returns>
    public static bool DeclaresInvariantGlobalization(string childAssemblyPath)
    {
        var path = RuntimeConfigPath(childAssemblyPath);

        return path is not null && InvariantByRuntimeConfig.GetOrAdd(path, static file => ReadsInvariantProperty(file));
    }

    /// <summary>The runtimeconfig beside the child assembly, or <see langword="null"/> when there is no assembly.</summary>
    /// <param name="childAssemblyPath">The child assembly, or an empty string.</param>
    /// <returns>The sibling <c>.runtimeconfig.json</c> path.</returns>
    public static string? RuntimeConfigPath(string childAssemblyPath) =>
        string.IsNullOrEmpty(childAssemblyPath)
            ? null
            : Path.ChangeExtension(childAssemblyPath, ".runtimeconfig.json");

    private static bool ReadsInvariantProperty(string runtimeConfigPath)
    {
        try
        {
            if (!File.Exists(runtimeConfigPath))
            {
                return false;
            }

            return ReadsInvariantTrue(File.ReadAllBytes(runtimeConfigPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable runtimeconfig is not a reason to start an unlocalized child that could not read it
            // either: the environment variable is set instead, which is the value that changes nothing it parses.
            return false;
        }
    }

    private static bool ReadsInvariantTrue(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(
            json,
            new JsonReaderOptions
            {
                MaxDepth = 16,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });

        string? lastName = null;

        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        lastName = reader.GetString();
                        break;

                    case JsonTokenType.True when string.Equals(lastName, InvariantGlobalizationProperty, StringComparison.Ordinal):
                        return true;

                    default:
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}
