// The columns the scan writes are one-way, and the database, not the handler, is what makes them so: this asserts the
// guard from the app role's side, with the direction the scan itself takes as the positive control.
using Evidgate.Modules.Evidence.Domain;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceSecretScanTaskTests
{
    /// <summary>
    /// A raise is the write the scan owns; the erasure of it is not. The app role's <c>sensitivity</c> update is granted
    /// for the scan, so the guard is what refuses <c>sensitive</c> back to <c>normal</c> — and the refused statement
    /// leaves the row as it was.
    /// </summary>
    [Fact]
    public async Task Sensitivity_Returning_To_Normal_Is_Refused_For_The_App()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world, CleanBody);

        await AppUpdateAsync(world, seeded.ArtifactId, "sensitivity = 'sensitive'");
        (await ColumnAsync(world, seeded.ArtifactId, "sensitivity")).ShouldBe(ArtifactSensitivity.Sensitive);

        (await AppUpdateErrorAsync(world, seeded.ArtifactId, "sensitivity = 'normal'")).SqlState.ShouldBe("23514");
        (await ColumnAsync(world, seeded.ArtifactId, "sensitivity")).ShouldBe(
            ArtifactSensitivity.Sensitive,
            "a refused update writes nothing");
    }

    /// <summary>
    /// A decided scan is not re-queued for a second look at bytes an operator may by then distrust. <c>pending</c> to a
    /// verdict is the allowed direction (the control), the verdict back to <c>pending</c> is refused, and the artifact
    /// keeps the verdict it had.
    /// </summary>
    [Fact]
    public async Task Decided_Scan_Returning_To_Pending_Is_Refused_For_The_App()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world, CleanBody);

        await AppUpdateAsync(world, seeded.ArtifactId, "scan_state = 'flagged'");
        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Flagged);

        (await AppUpdateErrorAsync(world, seeded.ArtifactId, "scan_state = 'pending'")).SqlState.ShouldBe("23514");
        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(
            ArtifactScanState.Flagged,
            "a refused update writes nothing");
    }

    /// <summary>
    /// One verdict never turns into the other, whoever asks: the app's own <c>pending</c> to <c>clean</c> write is the
    /// control, <c>clean</c> to <c>flagged</c> is refused, and the row keeps the verdict it had — so a caller cannot
    /// overwrite a decision the scan made.
    /// </summary>
    [Fact]
    public async Task Clean_Scan_Becoming_Flagged_Is_Refused_For_The_App()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world, CleanBody);

        await AppUpdateAsync(world, seeded.ArtifactId, "scan_state = 'clean'");
        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Clean);

        (await AppUpdateErrorAsync(world, seeded.ArtifactId, "scan_state = 'flagged'")).SqlState.ShouldBe("23514");
        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(
            ArtifactScanState.Clean,
            "a refused update writes nothing");
    }

    /// <summary>
    /// The other direction of the same rule: <c>flagged</c> to <c>clean</c> is the write that would hide a finding, so
    /// it is refused with the verdict kept. <c>pending</c> to <c>flagged</c> is the control the refusal is measured
    /// against.
    /// </summary>
    [Fact]
    public async Task Flagged_Scan_Returning_To_Clean_Is_Refused_For_The_App()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world, CleanBody);

        await AppUpdateAsync(world, seeded.ArtifactId, "scan_state = 'flagged'");
        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Flagged);

        (await AppUpdateErrorAsync(world, seeded.ArtifactId, "scan_state = 'clean'")).SqlState.ShouldBe("23514");
        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(
            ArtifactScanState.Flagged,
            "a refused update writes nothing");
    }

    /// <summary>
    /// Writing the verdict a row already holds is not a transition, so the guard lets it through: a retried or
    /// replayed write that sets the same value stays a successful no-op rather than a failure the queue dead-letters.
    /// </summary>
    [Fact]
    public async Task Rewriting_The_Verdict_A_Row_Holds_Is_Allowed_For_The_App()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world, CleanBody);

        await AppUpdateAsync(world, seeded.ArtifactId, "scan_state = 'clean'");
        await AppUpdateAsync(world, seeded.ArtifactId, "scan_state = 'clean'");

        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Clean);
    }

    /// <summary>One committed UPDATE of the row as <c>evidgate_app</c>, inside its own tenant scope.</summary>
    /// <param name="world">The tenant the artifact belongs to.</param>
    /// <param name="artifactId">The row to update.</param>
    /// <param name="assignment">The <c>SET</c> clause under test.</param>
    private async Task AppUpdateAsync(World world, Guid artifactId, string assignment)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync($"update public.artifact set {assignment} where id = @id", ("id", artifactId)))
            .ShouldBe(1, "the app role must see its own artifact");
        await scope.CommitAsync();
    }

    /// <summary>
    /// The same UPDATE where the guard must answer: the error the server raised is the test's subject, so the scope is
    /// rolled back rather than committed.
    /// </summary>
    /// <param name="world">The tenant the artifact belongs to.</param>
    /// <param name="artifactId">The row to update.</param>
    /// <param name="assignment">The <c>SET</c> clause the guard must refuse.</param>
    private async Task<PostgresException> AppUpdateErrorAsync(World world, Guid artifactId, string assignment)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        return await scope.CaptureErrorAsync($"update public.artifact set {assignment} where id = @id", ("id", artifactId));
    }
}
