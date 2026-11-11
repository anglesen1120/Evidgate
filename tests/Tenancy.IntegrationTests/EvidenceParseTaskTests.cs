// 06.3.6-T1.4 (AC3, AC8 second half) on the path the criteria name: the `artifact_parse` task handler over the real
// Api composition, a real PostgreSQL and a real SeaweedFS container. The runner is the one seam the test replaces, so
// the mapping from a failed child run to a `rejected` reason, the guarded write, and the no-op paths are the
// production ones. Expectations come from the plan (06.3.6 §2 AC3/AC8, §6, §7, §8) and the coordinator decision that
// fixes the exit-code mapping, not from the current behaviour.
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The <c>artifact_parse</c> task handler (06.3.6-T1.4) against the shared migrated database and one SeaweedFS
/// container: a failed run becomes <c>rejected</c> with the mapped <c>parse_*</c> code, a run whose stream carries
/// the child's own <c>rejected</c> record is rejected with that reason (D-S125-Q4), an object over the kind's cap is
/// rejected <c>too_large</c>, a successful record stream and every no-op path write nothing, an infrastructure failure is
/// retried rather than judged, a lost race is retried instead of read as a verdict, a cancellation is never a verdict,
/// a row whose key is not its minted final key is never read, only the redacted stderr excerpt is logged, and a
/// rejection is written once whatever the number of attempts.
/// </summary>
/// <remarks>
/// The runner is replaced by <see cref="StubParseRunner"/> — the one seam this task adds — so the test drives the exact
/// <see cref="ParseRunResult"/> shapes the real child produces (measured in 06.3.6-T1.3) without spawning a process.
/// Everything else is production: the tenant scope, the EF write model, the guarded <c>UPDATE</c>, the store read and
/// the object storage adapter.
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class EvidenceParseTaskTests(PostgresTenancyFixture fixture, SeaweedFsStore store, TenancyHostPool hosts)
    : IClassFixture<SeaweedFsStore>, IClassFixture<TenancyHostPool>
{
    /// <summary>The task's wire name (06.3.6 §5).</summary>
    private const string ParseTask = "artifact_parse";

    /// <summary>The media type of the report the handler reads (XML family → the <c>Xml</c> arm).</summary>
    private const string XmlType = "application/xml";

    /// <summary>A well-formed execution key: sha256 hex (00 §7), the key the artifact's pair carries.</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A small well-formed report body: the handler never interprets it, the child would.</summary>
    private static readonly byte[] ReportBody =
        "<report><testcase name=\"checkout\" outcome=\"passed\"/></report>"u8.ToArray();

    private static readonly string ReportChecksum = "sha256:" + Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(ReportBody));

    private readonly PostgresTenancyFixture _fixture = fixture;

    private readonly SeaweedFsStore _store = store;

    private readonly TenancyHostPool _hosts = hosts;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record World(
        TenancyApiFactory Host,
        string Database,
        Guid Org,
        Guid Project,
        Guid RunId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Host.DisposeAsync();
    }

    /// <summary>One seeded row: a <c>raw_report</c> artifact whose bytes the store really holds.</summary>
    private sealed record Seeded(Guid ArtifactId, string ObjectKey);

    /// <summary>
    /// Every failed-run shape the child can produce (06.3.6-T1.3 measurements) and the reason the mapper gives it.
    /// The failure kind travels as its name because <see cref="ParseRunFailureKind"/> is internal: a public theory may
    /// only close over public types, and the name keeps the test case readable in a report.
    /// <para>
    /// <b>The two limit signals.</b> The child sets <c>RLIMIT_CPU</c> with soft equal to its seconds and hard equal to
    /// seconds + 1, so the kernel's <c>SIGXCPU</c> reaches it first and the run ends at <c>128 + 24 = 152</c> —
    /// <see cref="ParseRunRejection.Timeout"/>. Exit 137 (<c>128 + SIGKILL</c>) is therefore never the CPU limit: it is
    /// the kernel OOM killer or an outside <c>SIGKILL</c>, and a parent cannot read the child's CPU time after it was
    /// reaped, so it stays <see cref="ParseRunRejection.Crashed"/> rather than being guessed as a limit that was
    /// applied. A wall-clock expiry is attributed before the exit code is consulted, so the runner's own kill is
    /// <see cref="ParseRunRejection.Timeout"/> whatever the code.
    /// </para>
    /// </summary>
    public static TheoryData<string, int?, string> FailureMapping => new()
    {
        { nameof(ParseRunFailureKind.ExitedAbnormally), 4, ParseRunRejection.Oom },
        { nameof(ParseRunFailureKind.ExitedAbnormally), 152, ParseRunRejection.Timeout },
        { nameof(ParseRunFailureKind.ExitedAbnormally), 137, ParseRunRejection.Crashed },
        { nameof(ParseRunFailureKind.ExitedAbnormally), 3, ParseRunRejection.Crashed },
        { nameof(ParseRunFailureKind.WallClockExceeded), 137, ParseRunRejection.Timeout },
        { nameof(ParseRunFailureKind.OutputBytesExceeded), 0, ParseRunRejection.Crashed },
        { nameof(ParseRunFailureKind.OutputRecordsExceeded), 0, ParseRunRejection.Crashed },
        { nameof(ParseRunFailureKind.MalformedOutput), 0, ParseRunRejection.Crashed },
    };
}
