using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.Modules.Authoring.Application.Reports;
using Evidgate.Modules.Authoring.Application.Reports.Sections;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The pure part of the report snapshot: the same source builds the same bytes, and a request the build cannot serve is refused with no database.</summary>
public sealed class ReportSnapshotTests
{
    private static readonly Guid NodeA = Guid.Parse("00000000-0000-7000-8000-00000000000a");
    private static readonly string[] TwoSections = ["a", "b"];
    private static readonly Guid NodeB = Guid.Parse("00000000-0000-7000-8000-00000000000b");

    [Fact]
    public void Build_SameSourceTwice_GivesTheSameHashAndBytes()
    {
        var registry = Registry();

        var first = ReportSnapshotBuilder.Build(Source(), registry, CancellationToken.None);
        var second = ReportSnapshotBuilder.Build(Source(), registry, CancellationToken.None);

        second.ContentHash.ShouldBe(first.ContentHash);
        second.CanonicalPayload.ShouldBe(first.CanonicalPayload);
        first.ContentHash.ShouldBe(CanonicalHash.Compute(first.CanonicalPayload));
        first.ContentHash.ShouldStartWith("sha256:");
    }

    [Fact]
    public void Build_LanguageScopeAndSelectionAreInsideTheHash()
    {
        var registry = Registry();
        var baseline = ReportSnapshotBuilder.Build(Source(), registry, CancellationToken.None).ContentHash;

        ReportSnapshotBuilder.Build(Source(language: "en"), registry, CancellationToken.None).ContentHash.ShouldNotBe(baseline);
        ReportSnapshotBuilder.Build(Source(scope: ReportScopeKinds.Requirements), registry, CancellationToken.None).ContentHash.ShouldNotBe(baseline);
        ReportSnapshotBuilder.Build(Source(ids: [NodeA, NodeB]), registry, CancellationToken.None).ContentHash.ShouldNotBe(baseline);
        ReportSnapshotBuilder.Build(Source(strategy: "smoke"), registry, CancellationToken.None).ContentHash.ShouldNotBe(baseline);
    }

    [Fact]
    public void Build_PinsTheVersionsTheSectionsWereBuiltFrom()
    {
        var requirement = new ReportRequirement(Guid.NewGuid(), "user_story", "t", null, null, null, Guid.NewGuid(), 3, "body", "sha256:" + new string('a', 64));
        var testCase = new ReportCase(Guid.NewGuid(), Guid.NewGuid(), "TC-1", "t", null, null, JsonDocument.Parse("{}").RootElement, "high", "approved", 5, "sha256:" + new string('b', 64));
        var source = Source() with { Requirements = [requirement], Cases = [testCase] };

        var pins = JsonNode.Parse(ReportSnapshotBuilder.Build(source, Registry(), CancellationToken.None).PinnedVersions)!;

        pins["requirements"]![0]!["version_no"]!.GetValue<int>().ShouldBe(3);
        pins["requirements"]![0]!["version_id"]!.GetValue<string>().ShouldBe(requirement.VersionId.ToString("D"));
        pins["cases"]![0]!["version_no"]!.GetValue<int>().ShouldBe(5);
    }

    [Fact]
    public void Build_SectionWithoutBuilder_FailsAndPublishesNothing()
    {
        var empty = new ReportSectionRegistry([]);

        var error = Should.Throw<ReportBuildException>(() => ReportSnapshotBuilder.Build(Source(), empty, CancellationToken.None));

        error.Message.ShouldStartWith(ReportBuildException.Failed + ":");
    }

    [Fact]
    public void Build_PayloadOverTheCap_IsRefusedNotTruncated()
    {
        var error = Should.Throw<ReportBuildException>(
            () => ReportSnapshotBuilder.Build(Source(strategy: "oversize"), Registry(), CancellationToken.None));

        error.Message.ShouldStartWith(ReportBuildException.TooLarge + ":");
    }

    [Fact]
    public void Registry_NamesTheFirstSectionNoBuilderServes()
    {
        var registry = Registry();

        registry.FirstUnsupported([TestReportSectionBuilder.Id]).ShouldBeNull();
        registry.FirstUnsupported([TestReportSectionBuilder.Id, "execution"]).ShouldBe(1);
        new ReportSectionRegistry([]).FirstUnsupported([TestReportSectionBuilder.Id]).ShouldBe(0);
        Should.Throw<ArgumentException>(() => new ReportSectionRegistry([new TestReportSectionBuilder(), new TestReportSectionBuilder()]));
    }

    [Theory]
    [InlineData("""{"scope_kind":"nope","selected_ids":["00000000-0000-7000-8000-00000000000a"],"sections":["s"],"language":"vi"}""", "/scope_kind", "invalid_value")]
    [InlineData("""{"scope_kind":"feature_nodes","selected_ids":[],"sections":["s"],"language":"vi"}""", "/selected_ids", "out_of_range")]
    [InlineData("""{"scope_kind":"feature_nodes","selected_ids":["00000000-0000-7000-8000-00000000000a","00000000-0000-7000-8000-00000000000a"],"sections":["s"],"language":"vi"}""", "/selected_ids/1", "duplicate")]
    [InlineData("""{"scope_kind":"feature_nodes","selected_ids":["x"],"sections":["s"],"language":"vi"}""", "/selected_ids/0", "invalid_type")]
    [InlineData("""{"scope_kind":"feature_nodes","selected_ids":["00000000-0000-7000-8000-00000000000a"],"sections":[],"language":"vi"}""", "/sections", "out_of_range")]
    [InlineData("""{"scope_kind":"feature_nodes","selected_ids":["00000000-0000-7000-8000-00000000000a"],"sections":["a","a"],"language":"vi"}""", "/sections/1", "duplicate")]
    [InlineData("""{"scope_kind":"feature_nodes","selected_ids":["00000000-0000-7000-8000-00000000000a"],"sections":["a","b","c","d","e"],"language":"vi"}""", "/sections", "out_of_range")]
    [InlineData("""{"scope_kind":"feature_nodes","selected_ids":["00000000-0000-7000-8000-00000000000a"],"sections":["a"],"language":"fr"}""", "/language", "invalid_value")]
    [InlineData("""{"scope_kind":"feature_nodes","selected_ids":["00000000-0000-7000-8000-00000000000a"],"sections":["a"]}""", "/language", "required")]
    [InlineData("""{"scope_kind":"feature_nodes","selected_ids":["00000000-0000-7000-8000-00000000000a"],"sections":["a"],"language":"vi","strategy":5}""", "/strategy", "invalid_type")]
    public void Body_Refuses_WithThePathAndReasonOfTheMistake(string json, string path, string reason)
    {
        using var document = JsonDocument.Parse(json);

        var read = TestReportBody.Read(document.RootElement);

        read.IsSuccess.ShouldBeFalse();
        var detail = read.Error!.Details!.Single();
        detail.Path.ShouldBe(path);
        detail.Reason.ShouldBe(reason);
    }

    [Fact]
    public void Body_TextOver2000CodePoints_IsRefused_And2000AstralCharactersPass()
    {
        string Body(string text) => $$"""{"scope_kind":"requirements","selected_ids":["{{NodeA}}"],"sections":["a"],"language":"en","environment":"{{text}}"}""";

        using var over = JsonDocument.Parse(Body(new string('a', 2001)));
        using var edge = JsonDocument.Parse(Body(string.Concat(Enumerable.Repeat("\U0001D11E", 2000))));

        TestReportBody.Read(over.RootElement).Error!.Details!.Single().Reason.ShouldBe("too_long");
        TestReportBody.Read(edge.RootElement).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Body_SortsAndKeepsTheSelectionSoTheSameSetHashesTheSame()
    {
        var ids = new[] { NodeB, NodeA };
        var json = JsonSerializer.Serialize(new
        {
            scope_kind = "feature_nodes",
            selected_ids = ids,
            sections = TwoSections,
            language = "vi",
        });
        using var document = JsonDocument.Parse(json);

        var request = TestReportBody.Read(document.RootElement).Value;

        request.SelectedIds.ShouldBe([NodeA, NodeB]);
        request.Sections.ShouldBe(["a", "b"]);
        request.Strategy.ShouldBeNull();
    }

    [Fact]
    public void Scope_OnlyFeatureNodesAndRequirementsHaveASource()
    {
        ReportScopeKinds.IsSupported(ReportScopeKinds.FeatureNodes).ShouldBeTrue();
        ReportScopeKinds.IsSupported(ReportScopeKinds.Requirements).ShouldBeTrue();
        ReportScopeKinds.IsSupported(ReportScopeKinds.ManualRun).ShouldBeFalse();
        ReportScopeKinds.IsSupported(ReportScopeKinds.Release).ShouldBeFalse();
        ReportScopeKinds.IsKnown(ReportScopeKinds.ManualRun).ShouldBeTrue();
    }

    [Fact]
    public void Matrix_BuiltTwiceForOneSource_IsTheSameInstance()
    {
        var source = ReportSectionFixture.Source();

        TraceabilityMatrix.For(source).ShouldBeSameAs(TraceabilityMatrix.For(source));
    }

    [Fact]
    public void Matrix_ItemLinksExpandingPastTheRowCap_AreRefusedAsTooLargeBeforeAnyRowIsBuilt()
    {
        // 400 cases on one item reached by 400 item links is 160000 rows, over the cap; each input list is small.
        var source = ReportSectionFixture.Source();
        var template = source.Cases[0];
        var cases = Enumerable.Range(0, 400).Select(n => template with { Id = ReportSectionFixture.Id(1000 + n), Code = "TC-X" + n });
        var links = Enumerable.Range(0, 400).Select(n => new ReportLink(
            ReportSectionFixture.Id(2000 + n), ReportSectionFixture.Covered, ReportSectionFixture.CurrentVersion, 2, ReportSectionFixture.Item, null));
        var oversize = source with { Cases = [.. cases], Links = [.. links] };

        var error = Should.Throw<ReportBuildException>(() => TraceabilityMatrix.For(oversize));

        error.Message.ShouldStartWith(ReportBuildException.TooLarge + ":");
        (400 * 400).ShouldBeGreaterThan(TestReportLimits.MaxTraceRows);
    }

    [Theory]
    [InlineData("a")] // 1 byte a character
    [InlineData("\u20AC")] // BMP character: 3 bytes raw
    [InlineData("\u0001")] // control character: escaped to 6 bytes, the widest a character grows
    [InlineData("\"")] // quote: escaped to 2 bytes
    public void TaskPayload_AtTheRequestLimits_FitsTheQueuePayloadCap_WhateverTheText(string character)
    {
        var text = string.Concat(Enumerable.Repeat(character, TestReportLimits.MaxTextCodePoints));

        var json = TestReportTasks.ToJson(Payload(text, text));

        TestReportTasks.Fits(json).ShouldBeTrue();
        Encoding.UTF8.GetByteCount(json).ShouldBeLessThanOrEqualTo(TestReportLimits.MaxTaskPayloadBytes);
    }

    [Fact]
    public void TaskPayload_AstralTextAtTheRequestLimits_IsOverTheCap_SoTheCreateRefusesItExplicitly()
    {
        // The encoder writes a character outside the BMP as a 12 byte surrogate pair escape; two such texts at the
        // limits plus 1000 ids pass 64 KiB, and Fits is what turns that into the 422 instead of a queue 500.
        var astral = string.Concat(Enumerable.Repeat("\U0001D11E", TestReportLimits.MaxTextCodePoints));
        var over = TestReportTasks.ToJson(Payload(astral, astral));
        var under = TestReportTasks.ToJson(Payload(new string('a', 20000), null));

        TestReportTasks.Fits(over).ShouldBeFalse();
        TestReportTasks.Fits(under).ShouldBeTrue();
    }

    private static ReportJobPayload Payload(string? strategy, string? environment) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        ReportScopeKinds.FeatureNodes,
        [.. Enumerable.Range(0, TestReportLimits.MaxSelectedIds).Select(_ => Guid.NewGuid())],
        ["a", "b", "c", "d"],
        "vi",
        strategy,
        environment);

    [Theory]
    [InlineData(false, null, null, "pending", null)]
    [InlineData(true, "queued", null, "pending", null)]
    [InlineData(true, "active", null, "running", null)]
    [InlineData(true, "retry", "ReportBuildException: build_failed: x", "retry", null)]
    [InlineData(true, "done", null, "failed", "build_failed")] // the job query re-checks publication before it answers this
    [InlineData(true, "dead_letter", "ReportBuildException: report_too_large: the snapshot is larger", "failed", "report_too_large")]
    [InlineData(true, "dead_letter", "ReportBuildException: build_failed: the project is not live", "failed", "build_failed")]
    [InlineData(true, "dead_letter", "NpgsqlException: password=hunter2", "failed", "build_failed")]
    public void JobState_MapsTheQueueRow_AndNeverEchoesTheRawError(bool dispatched, string? taskState, string? lastError, string state, string? reason)
    {
        var job = TestReportJobStates.Map(
            Guid.Empty,
            new TestReportJobTaskRow { Dispatched = dispatched, TaskState = taskState, LastError = lastError });

        job.State.ShouldBe(state);
        job.FailureReason.ShouldBe(reason);
        job.ReportId.ShouldBeNull();
    }

    private static ReportSectionRegistry Registry() => new([new TestReportSectionBuilder()]);

    private static ReportSource Source(
        string scope = ReportScopeKinds.FeatureNodes,
        string language = "vi",
        Guid[]? ids = null,
        string? strategy = null) =>
        new(
            new ReportRequest(scope, ids ?? [NodeA], [TestReportSectionBuilder.Id], language, strategy, null),
            [new ReportNode(NodeA, null, "module", "Root", 0)],
            [],
            [],
            [],
            []);
}
