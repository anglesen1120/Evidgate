using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Foundation.IntegrationTests;

/// <summary>
/// 01.6.1-T2.2 (AC2): Respawn empties the shared harness database between two
/// tests, and the migration journal survives the reset.
/// <para>
/// The two halves of <c>Respawn_TwoTestsSeeEmptyDb</c> each assert the table is
/// empty <em>before</em> they write, then write one row and assert it is the only
/// one. That shape is what makes the pair order-independent: xUnit does not promise
/// the order of two test methods, and a pair where only the first writes passes in
/// the "observer first, inserter second" order even when the reset is broken. With
/// both halves writing, the second test to run always sees the first one's row when
/// Respawn did not run — whichever half is second.
/// </para>
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class RespawnTests : HarnessTest
{
    private const string InsertProbe =
        "insert into public.rls_probe (org_id, id, label) values (@org_id, @id, @label)";

    private static readonly Guid ProbeOrg = Guid.Parse("0a0a0a0a-0a0a-7a0a-8a0a-0a0a0a0a0a0a");

    public RespawnTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>Test A: this test's row is the first and only one — the reset ran before it.</summary>
    [Fact]
    public async Task Respawn_TwoTestsSeeEmptyDb_InsertsARow()
    {
        (await Fixture.CountProbeRowsAsync(cancellationToken: Token)).ShouldBe(0L);

        await Fixture.SeedAsync(
            InsertProbe,
            [("org_id", ProbeOrg), ("id", Guid.CreateVersion7()), ("label", "test A")],
            Token);

        (await Fixture.CountProbeRowsAsync(cancellationToken: Token)).ShouldBe(1L);
    }

    /// <summary>
    /// Test B: the table test A wrote to is empty again — the reset ran in between,
    /// and this test's own write is again the only row.
    /// </summary>
    [Fact]
    public async Task Respawn_TwoTestsSeeEmptyDb_ObservesAnEmptyTable()
    {
        (await Fixture.CountProbeRowsAsync(cancellationToken: Token)).ShouldBe(0L);

        await Fixture.SeedAsync(
            InsertProbe,
            [("org_id", ProbeOrg), ("id", Guid.CreateVersion7()), ("label", "test B")],
            Token);

        (await Fixture.CountProbeRowsAsync(cancellationToken: Token)).ShouldBe(1L);
    }

    /// <summary>
    /// The outcome of the reset rather than its mechanism: Respawn builds one
    /// truncate plan from the catalog when the harness starts, so a table it did not
    /// cover would keep a previous test's rows. The census is read through a
    /// superuser, the only role whose view is not filtered by row level security, and
    /// it is asserted non-empty before the reset so an always-empty census cannot
    /// pass this test by accident.
    /// </summary>
    [Fact]
    public async Task Respawn_LeavesNoRowInAnyApplicationTable()
    {
        await Fixture.SeedProjectAsync(cancellationToken: Token);
        await Fixture.SeedAsync(
            InsertProbe,
            [("org_id", ProbeOrg), ("id", Guid.CreateVersion7()), ("label", "census")],
            Token);

        (await Fixture.NonEmptyApplicationTablesAsync(Token)).ShouldNotBeEmpty();

        await Fixture.ResetAsync(Token);

        (await Fixture.NonEmptyApplicationTablesAsync(Token)).ShouldBeEmpty();
    }

    /// <summary>
    /// The reset itself, proven inside one test so the check does not depend on the
    /// execution order at all: a row (and the head of the journal) before, an empty
    /// table and an unchanged journal after.
    /// </summary>
    [Fact]
    public async Task Respawn_PreservesMigrationJournal()
    {
        var headBefore = await Fixture.JournalHeadAsync(Token);
        var countBefore = await Fixture.JournalCountAsync(Token);

        await Fixture.SeedAsync(
            InsertProbe,
            [("org_id", ProbeOrg), ("id", Guid.CreateVersion7()), ("label", "journal test")],
            Token);
        (await Fixture.CountProbeRowsAsync(cancellationToken: Token)).ShouldBe(1L);

        await Fixture.ResetAsync(Token);

        // The data is gone...
        (await Fixture.CountProbeRowsAsync(cancellationToken: Token)).ShouldBe(0L);

        // ...and the Migrator's journal is not: a reset that truncated
        // public.schema_migration would take the readiness state (01.1.2-T1.3) with
        // it, and every later test of this project would fail as "behind head".
        (await Fixture.JournalHeadAsync(Token)).ShouldBe(headBefore);
        (await Fixture.JournalCountAsync(Token)).ShouldBe(countBefore);
    }
}
