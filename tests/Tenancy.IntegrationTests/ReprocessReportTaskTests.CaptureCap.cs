// The byte cap of a reprocess is the capture policy of the report's RUN's project, the project the write transaction
// charges, even when the artifact row names another project of the same org: the row's project column ties to no key of
// the run's, so a resolution that asked the row would read a report the run's project never admitted.
using System.Globalization;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReprocessReportTaskTests
{
    [Fact]
    public async Task A_Cap_Of_The_Runs_Project_Refuses_A_Report_Whose_Artifact_Names_Another_Project()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var otherProject = await FixtureSeed.ProjectAsync(_fixture, world.Database, world.Org);

        // The artifact row names a sibling project that has no row of its own, so the kind's 256 MiB default would
        // admit the report; only the run's project carries the 8-byte cap this world admits nothing under.
        var artifactId = await SeedArtifactAsync(world, ReportBody, otherProject);
        var sourceReportId = await SeedSourceReportAsync(world, artifactId);
        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);
        await SeedRawReportCapAsync(world, world.Project, 8);

        var outcome = await ReprocessAsync(world, parsingId);

        // Under the artifact's own project the fetch would have had room and the attempt would have mapped a report the
        // run's project never admitted; the cap of the run's project refuses before a byte is read.
        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        outcome.Reason.ShouldBe("too_large");
        (await CountAsync(world, "event")).ShouldBe(0);
        await AssertRefusalRowAsync(world, parsingId);
        await AssertSourceReportUntouchedAsync(world, sourceReportId);
    }

    [Fact]
    public async Task A_Cap_Of_The_Runs_Project_Refuses_A_Report_Whose_Artifact_Names_That_Project()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var sourceReportId = await SeedSourceReportAsync(world, artifactId);
        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);
        await SeedRawReportCapAsync(world, world.Project, 8);

        var outcome = await ReprocessAsync(world, parsingId);

        // The row and the run name one project, so both resolutions read the same policy and the refusal is unchanged.
        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        outcome.Reason.ShouldBe("too_large");
        (await CountAsync(world, "event")).ShouldBe(0);
        await AssertRefusalRowAsync(world, parsingId);
        await AssertSourceReportUntouchedAsync(world, sourceReportId);
    }

    [Fact]
    public async Task A_Cap_Of_The_Artifacts_Own_Project_Does_Not_Bind_Its_Runs_Report()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var otherProject = await FixtureSeed.ProjectAsync(_fixture, world.Database, world.Org);
        var artifactId = await SeedArtifactAsync(world, ReportBody, otherProject);
        var sourceReportId = await SeedSourceReportAsync(world, artifactId);
        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);
        await SeedRawReportCapAsync(world, otherProject, 8);

        var outcome = await ReprocessAsync(world, parsingId);

        // Only the artifact's own project carries the cap, so this is the positive control of the refusal above: with
        // the cap moved to the row's project and nothing else changed, the run's own default admits the report.
        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);
    }

    /// <summary>The refusal row is the attempt's own, stamped with the release the request pinned and no counter.</summary>
    /// <param name="world">The world the attempt ran in.</param>
    /// <param name="parsingId">The request id, which is also the refusal row's id.</param>
    private async Task AssertRefusalRowAsync(World world, Guid parsingId) =>
        (await _fixture.ScalarAsync<string>(
            world.Database,
            "select outcome || '|' || coalesce(reason, '-') || '|' || parser_version || '|' || observed_tests::text "
            + $"from public.raw_report where id = '{parsingId:D}'"))
            .ShouldBe($"malformed|too_large|{PinVersion}|0");

    /// <summary>The source report the request re-reads is untouched, so the refusal added a row and rewrote nothing.</summary>
    /// <param name="world">The world the attempt ran in.</param>
    /// <param name="sourceReportId">The report the original parse committed.</param>
    private async Task AssertSourceReportUntouchedAsync(World world, Guid sourceReportId) =>
        (await _fixture.ScalarAsync<string>(
            world.Database,
            "select outcome || '|' || coalesce(reason, '-') || '|' || parser_version || '|' || observed_tests::text "
            + $"from public.raw_report where id = '{sourceReportId:D}'"))
            .ShouldBe($"mapped|-|{SourceVersion}|1");

    /// <summary>
    /// One <c>raw_report</c> cap for one project, so a case can put the run's project and the artifact's own project on
    /// different policies (the resolution takes the lower of the row and the kind's default).
    /// </summary>
    /// <param name="world">The tenant the policy belongs to.</param>
    /// <param name="projectId">The project the row caps.</param>
    /// <param name="maxBytes">The project's ceiling for the kind.</param>
    private Task SeedRawReportCapAsync(World world, Guid projectId, long maxBytes) =>
        _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity) "
            + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{projectId:D}', '{ArtifactKind.RawReport}', "
            + $"{maxBytes.ToString(CultureInfo.InvariantCulture)}, array['{XmlType}'], 'normal')");
}
