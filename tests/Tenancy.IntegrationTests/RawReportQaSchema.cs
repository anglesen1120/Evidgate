using System.Text.Json;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportParseTests
{
    [Theory]
    [InlineData("update public.raw_report set declared_tests = 9 where artifact_id = @artifact")]
    [InlineData("delete from public.raw_report where artifact_id = @artifact")]
    public async Task RawReport_AppMutation_IsDeniedAgainstItsOwnVisibleCommittedRow(string statement)
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifact = await SeedArtifactAsync(world, MappedBody);
        (await ParseAsync(world, artifact, ProducerKind)).Events.ShouldBe(1);
        var original = (await RawReportAsync(world, artifact)).GetRawText();

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ScalarAsync<long>(
                "select count(*) from public.raw_report where artifact_id = @artifact", ("artifact", artifact))).ShouldBe(1);
            var error = await scope.CaptureErrorAsync(statement, ("artifact", artifact));
            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            error.MessageText.ShouldContain("permission denied");
        }

        (await RawReportAsync(world, artifact)).GetRawText().ShouldBe(original);
        (await CountAsync(world, "event", artifact)).ShouldBe(1);
    }

    [Fact]
    public async Task RawReport_AppInsertWithForeignOrgRun_IsRefusedByTheRunForeignKey()
    {
        var world = await StartAsync();
        await using var _ = world;
        var artifact = await SeedArtifactAsync(world, MappedBody);
        var (foreignOrg, _) = await FixtureSeed.TenantAsync(_fixture, world.Database);
        var foreignRun = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, foreignOrg);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            var error = await scope.CaptureErrorAsync(
                QaReportInsertSql,
                QaReportParameters(world, artifact, run: foreignRun));
            error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
            error.ConstraintName.ShouldBe("raw_report_run_fk");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(QaReportInsertSql, QaReportParameters(world, artifact))).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await RawReportCountAsync(world, artifact)).ShouldBe(1);
    }

    [Fact]
    public async Task RawReport_DiscrepancyArray_AdmitsSixteenEntriesButRefusesSeventeen()
    {
        var world = await StartAsync();
        await using var _ = world;
        var artifact = await SeedArtifactAsync(world, MappedBody);
        var sixteen = JsonSerializer.Serialize(Enumerable.Repeat(new { code = "count_mismatch", count = 1 }, 16));
        var seventeen = JsonSerializer.Serialize(Enumerable.Repeat(new { code = "count_mismatch", count = 1 }, 17));

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            var error = await scope.CaptureErrorAsync(
                QaReportInsertSql,
                QaReportParameters(world, artifact, outcome: "partial", discrepancies: seventeen));
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("raw_report_discrepancy_shape_check");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(
                QaReportInsertSql,
                QaReportParameters(world, artifact, outcome: "partial", discrepancies: sixteen))).ShouldBe(1);
            await scope.CommitAsync();
        }

        Discrepancies(await RawReportAsync(world, artifact)).Length.ShouldBe(16);
    }

    [Fact]
    public async Task RawReport_RefusalReason_RejectsInvalidWordsAndAcceptsOneAndSixtyFourCharacters()
    {
        var world = await StartAsync();
        await using var _ = world;
        var artifact = await SeedArtifactAsync(world, MappedBody);
        string?[] invalid = [null, "", "Malformed", "malformed\n", "bad-reason", "0reason", "réason", new string('a', 65)];

        foreach (var reason in invalid)
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
            var error = await scope.CaptureErrorAsync(
                QaReportInsertSql,
                QaReportParameters(world, artifact, outcome: "malformed", reason: reason));
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("raw_report_reason_check");
        }

        foreach (var reason in new[] { "a", new string('a', 64), "parse_crashed" })
        {
            var acceptedArtifact = await SeedArtifactAsync(world, MappedBody);
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
            (await scope.ExecuteAsync(
                QaReportInsertSql,
                QaReportParameters(world, acceptedArtifact, outcome: "malformed", reason: reason))).ShouldBe(1);
            await scope.CommitAsync();
            Text(await RawReportAsync(world, acceptedArtifact), "reason").ShouldBe(reason);
        }

        (await RawReportCountAsync(world, artifact)).ShouldBe(0);
    }

    [Theory]
    [InlineData("[\"arbitrary body instead of a discrepancy\"]")]
    [InlineData("[{\"code\":\"count_mismatch\",\"count\":-1}]")]
    [InlineData("[{\"code\":\"count_mismatch\",\"count\":1,\"message\":\"raw report body\"}]")]
    public async Task RawReport_DiscrepancyMembers_RejectNonCountObjectsAndMessageBodies(string invalid)
    {
        var world = await StartAsync();
        await using var _ = world;
        var artifact = await SeedArtifactAsync(world, MappedBody);
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(
                QaReportInsertSql,
                QaReportParameters(world, artifact, outcome: "partial", discrepancies: "[{\"code\":\"count_mismatch\",\"count\":1}]")))
                .ShouldBe(1);
            await scope.CommitAsync();
        }

        var rejectedArtifact = await SeedArtifactAsync(world, MappedBody);
        await using var refused = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await refused.CaptureErrorAsync(
            QaReportInsertSql,
            QaReportParameters(world, rejectedArtifact, outcome: "partial", discrepancies: invalid));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    private const string QaReportInsertSql = """
        insert into public.raw_report
            (id, org_id, run_id, artifact_id, producer_kind, parser_id, parser_version, outcome, reason, discrepancies)
        values
            (@id, @org, @run, @artifact, 'pytest-junitxml', 'junit-pytest', '1.0.0', @outcome, @reason::text, @discrepancies::jsonb)
        """;

    private static (string Name, object Value)[] QaReportParameters(
        World world,
        Guid artifact,
        Guid? run = null,
        string outcome = "mapped",
        string? reason = null,
        string discrepancies = "[]") =>
    [
        ("id", Guid.CreateVersion7()),
        ("org", world.Org),
        ("run", run ?? world.RunId),
        ("artifact", artifact),
        ("outcome", outcome),
        ("reason", reason ?? (object)DBNull.Value),
        ("discrepancies", discrepancies),
    ];
}
