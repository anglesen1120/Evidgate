using System.Text;

namespace Architecture.Tests.Rules;

/// <summary>
/// 00 §9.1: usage metering is an internal module call, never a public endpoint. Only the Usage module that owns the
/// ledger and the Ingestion module that meters an accepted batch may name <c>IUsageLedger</c>, and the metering handler
/// itself is DB-to-DB (00 §5.7 P5), so no HTTP client may appear in it. The usage task type has exactly one producer,
/// the ingest commit, plus the handler that consumes it — a reprocess path adding itself to that set is a double bill.
/// </summary>
public static class UsageMeteringRules
{
    /// <summary>Every hand-written C# file the rules scan lives here.</summary>
    public const string SourceRoot = "src/";

    /// <summary>The one task handler that meters an accepted batch into the ledger.</summary>
    public const string MeteringHandlerPath =
        "src/Modules/Evidgate.Modules.Ingestion/Application/MeterEventBatchTaskHandler.cs";

    private const string UsageTaskType = "usage.batch_accepted";

    private const string TaskTypeConstant = "UsageTaskType";

    /// <summary>The assembly (by its source directory) whose code owns the ledger.</summary>
    private static readonly string[] LedgerOwners =
    [
        "src/Modules/Evidgate.Modules.Usage/",
        "src/Modules/Evidgate.Modules.Usage.Contracts/",
    ];

    /// <summary>The one module outside Usage allowed to call the ledger: the ingest-side emitter.</summary>
    private static readonly string[] LedgerCallers =
    [
        "src/Modules/Evidgate.Modules.Ingestion/",
    ];

    /// <summary>Every file allowed to name the usage task type: its producer and its handler.</summary>
    private static readonly string[] Emitters =
    [
        "src/Modules/Evidgate.Modules.Ingestion/Application/Commands/IngestEventBatch.cs",
        MeteringHandlerPath,
    ];

    /// <summary>The HTTP references the metering step may never carry (00 §5.7 P5).</summary>
    private static readonly string[] HttpClientTokens = ["HttpClient", "System.Net.Http"];

    /// <summary>Files that name the ledger outside the modules that own or call it, one violation each.</summary>
    public static IReadOnlyList<string> FindLedgerReferenceViolations(IEnumerable<(string Path, string Text)> files) =>
        [.. files
            .Where(file => InSourceRoot(file.Path))
            .Where(file => Code(file.Text).Contains("IUsageLedger", StringComparison.Ordinal))
            .Where(file => !LedgerOwners.Concat(LedgerCallers).Any(owner => file.Path.StartsWith(owner, StringComparison.Ordinal)))
            .Select(file => $"{file.Path} references IUsageLedger outside the Usage module and its ingest caller")];

    /// <summary>Every HTTP reference of one file, one violation each (the metering step must stay DB-to-DB).</summary>
    public static IReadOnlyList<string> FindNetworkViolations(string path, string text) =>
        [.. HttpClientTokens
            .Where(token => Code(text).Contains(token, StringComparison.Ordinal))
            .Select(token => $"{path} references {token}: the metering step is a DB-to-DB task, never a network call")];

    /// <summary>
    /// Files that name the usage task type outside the emitter set, plus an emitter that stopped defining it: both mean
    /// the metering producer moved, which is the one thing this rule exists to notice.
    /// </summary>
    public static IReadOnlyList<string> FindEmitterViolations(IEnumerable<(string Path, string Text)> files)
    {
        var naming = files
            .Where(file => InSourceRoot(file.Path))
            .Where(file => MentionsUsageTask(file.Text))
            .Select(file => file.Path)
            .ToList();

        var violations = new List<string>();

        foreach (var path in naming.Except(Emitters, StringComparer.Ordinal))
        {
            violations.Add($"{path} names the usage task type '{UsageTaskType}'; only the ingest commit may produce it");
        }

        foreach (var path in Emitters.Except(naming, StringComparer.Ordinal))
        {
            violations.Add($"{path} no longer names the usage task type '{UsageTaskType}'; the emitter set moved");
        }

        return violations;
    }

    private static bool InSourceRoot(string path) => path.StartsWith(SourceRoot, StringComparison.Ordinal);

    private static bool MentionsUsageTask(string text) =>
        Code(text).Contains(UsageTaskType, StringComparison.Ordinal)
        || Code(text).Contains(TaskTypeConstant, StringComparison.Ordinal);

    /// <summary>
    /// The file's code with its <c>//</c> and <c>/* */</c> comments removed: a doc comment naming the ledger or a plan id
    /// is not a reference, and a rule that counted prose would fail on a composition root's own comment.
    /// </summary>
    private static string Code(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inBlock = false;

        foreach (var line in text.Split('\n'))
        {
            var current = line;

            if (inBlock)
            {
                var close = current.IndexOf("*/", StringComparison.Ordinal);
                if (close < 0)
                {
                    continue;
                }

                current = current[(close + 2)..];
                inBlock = false;
            }

            while (current.IndexOf("/*", StringComparison.Ordinal) is var start and >= 0)
            {
                var end = current.IndexOf("*/", start + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    current = current[..start];
                    inBlock = true;
                    break;
                }

                current = current[..start] + current[(end + 2)..];
            }

            var comment = current.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                current = current[..comment];
            }

            builder.Append(current).Append('\n');
        }

        return builder.ToString();
    }
}
