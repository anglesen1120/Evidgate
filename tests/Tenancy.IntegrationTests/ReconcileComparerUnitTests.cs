using System.Text.RegularExpressions;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.2-T2.2/T2.3 without a database: the pure declared-versus-observed comparer — every 00 §8.5 code it can name from
/// event evidence and from the event flags (<c>setup_error</c>, <c>stale_epoch</c>), the open-shard short circuit, the
/// fail-closed missing declaration, the three bounds a closed shard's declaration is checked against (count, end and the
/// contiguity rule of <c>contracts/events/ordering.md</c> §3), the ordinal reason order, the planned/closed shard counts
/// and the stored-event watermark — plus the pin that every literal is one of the closed list <c>migrations/0049</c>
/// enforces, and the requirement set's freeze bound (<see cref="RequiredArtifactResolver.Compose"/>). The database half
/// (the one grouped aggregate, the run lock) is exercised by the reconcile integration suite.
/// </summary>
public sealed partial class ReconcileComparerUnitTests
{
    // ---- argument guards -----------------------------------------------------------------

    [Fact]
    public void Compare_Rejects_Null_Lists()
    {
        Should.Throw<ArgumentNullException>(() => ReconcileComparer.CompareEvents(null!, []));
        Should.Throw<ArgumentNullException>(() => ReconcileComparer.CompareEvents([], null!));
        Should.Throw<ArgumentNullException>(() => ReconcileComparer.Compare(null!, [], ExecutionCoverage.Complete));
        Should.Throw<ArgumentNullException>(() => ReconcileComparer.Compare([], null!, ExecutionCoverage.Complete));
        Should.Throw<ArgumentNullException>(() => ReconcileComparer.Compare([], [], null!));
    }

    // ---- builders ------------------------------------------------------------------------

    private static PlannedShard Planned(
        string producerId,
        int shardIndex = 1,
        bool closed = true,
        long? declaredLastSequence = 0,
        int? declaredCount = 1) =>
        new(producerId, shardIndex, closed, declaredLastSequence, declaredCount);

    private static ObservedShard Observed(
        string producerId,
        long upTo = 1,
        long beyond = 0,
        long unknown = 0,
        long unfinished = 0,
        long? min = 0,
        long? max = 0,
        long blocked = 0,
        long stale = 0) =>
        new(producerId, upTo, beyond, min, max, unknown, unfinished, blocked, stale);

    /// <summary>
    /// The closed code list of the CHECK as the latest migration leaves it —
    /// <c>migrations/0056_reconcile_missing_result.sql</c>, which re-creates <c>evidence_revision_missing_check</c> over
    /// 0049's list plus the plan-coverage code — read from the <c>missing &lt;@ array[...]</c> term. This is the pin that
    /// this comparer's literals and the persisted CHECK cannot drift apart.
    /// </summary>
    private static string[] MigrationMissingCodes()
    {
        var path = Path.Combine(RepositoryRoot(), "migrations", "0056_reconcile_missing_result.sql");
        File.Exists(path).ShouldBeTrue($"the evidence_revision CHECK migration must be committed at {path}");

        var sql = File.ReadAllText(path);
        var start = sql.IndexOf("missing <@ array[", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1, "0056 must list the codes with 'missing <@ array['");

        var end = sql.IndexOf("]::text[]", start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, "the 0056 code list must close with ']::text[]'");

        return Regex.Matches(sql[start..end], "'([a-z_]+)'")
            .Select(match => match.Groups[1].Value)
            .ToArray();
    }

    /// <summary>The repository root: the directory holding <c>Evidgate.slnx</c> above the test binary.</summary>
    private static string RepositoryRoot()
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

        throw new InvalidOperationException($"Evidgate.slnx not found above {AppContext.BaseDirectory}");
    }
}
