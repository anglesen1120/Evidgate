using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.Modules.Authoring.Application.Reports;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed class QAReportBoundaryTests
{
    private static readonly string[] CanonicalId = ["00000000-0000-7000-8000-000000000001"];

    [Fact]
    public void Body_SelectedIdsAtTheLimit_AcceptsTheLimitAndRefusesOneMore()
    {
        var ids = Enumerable.Range(1, TestReportLimits.MaxSelectedIds + 1).Select(ReportSectionFixture.Id).ToArray();

        var accepted = TestReportBody.Read(Body(ids[..TestReportLimits.MaxSelectedIds]));
        var refused = TestReportBody.Read(Body(ids));

        accepted.IsSuccess.ShouldBeTrue();
        accepted.Value.SelectedIds.Count.ShouldBe(TestReportLimits.MaxSelectedIds);
        refused.IsSuccess.ShouldBeFalse();
        var detail = refused.Error!.Details!.Single();
        detail.Path.ShouldBe("/selected_ids");
        detail.Reason.ShouldBe("out_of_range");
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("00000000000070008000000000000001")]
    [InlineData("{00000000-0000-7000-8000-000000000001}")]
    public void Body_NoncanonicalOrEmptyUuid_RefusesWhileCanonicalUuidPasses(string invalid)
    {
        var accepted = TestReportBody.Read(Body(CanonicalId));
        var refused = TestReportBody.Read(Body(new[] { invalid }));

        accepted.IsSuccess.ShouldBeTrue();
        refused.IsSuccess.ShouldBeFalse();
        var detail = refused.Error!.Details!.Single();
        detail.Path.ShouldBe("/selected_ids/0");
        detail.Reason.ShouldBe("invalid_type");
    }

    [Fact]
    public void Body_SectionCountAtTheLimit_AcceptsTheLimitAndRefusesOneMore()
    {
        var ids = new[] { ReportSectionFixture.Feature };
        var accepted = TestReportBody.Read(Body(ids, ReportSectionFixture.AllSections));
        var refused = TestReportBody.Read(Body(ids, [.. ReportSectionFixture.AllSections, "extra"]));

        accepted.IsSuccess.ShouldBeTrue();
        accepted.Value.Sections.Count.ShouldBe(TestReportLimits.MaxSections);
        refused.IsSuccess.ShouldBeFalse();
        var detail = refused.Error!.Details!.Single();
        detail.Path.ShouldBe("/sections");
        detail.Reason.ShouldBe("out_of_range");
    }

    [Fact]
    public void Build_PayloadAtTheByteLimit_AcceptsTheLimitAndRefusesOneMore()
    {
        var accepted = SnapshotWithBytes(TestReportLimits.MaxPayloadBytes);

        ReportSnapshotBuilder.StoredBytes(accepted.CanonicalPayload).ShouldBe(TestReportLimits.MaxPayloadBytes);
        var error = Should.Throw<ReportBuildException>(() => SnapshotWithBytes(TestReportLimits.MaxPayloadBytes + 1));
        error.Message.ShouldStartWith(ReportBuildException.TooLarge + ":");
    }

    [Fact]
    public void Build_CanceledBeforeTheFirstSection_DoesNotInvokeTheBuilder()
    {
        var builder = new SizedSectionBuilder();
        var registry = new ReportSectionRegistry([builder]);
        var source = Source();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Should.Throw<OperationCanceledException>(() => ReportSnapshotBuilder.Build(source, registry, cancellation.Token));
        builder.Calls.ShouldBe(0);

        ReportSnapshotBuilder.Build(source, registry, CancellationToken.None).CanonicalPayload.ShouldNotBeNullOrEmpty();
        builder.Calls.ShouldBe(1);
    }

    internal static ReportSnapshot SnapshotWithBytes(int byteCount)
    {
        var builder = new SizedSectionBuilder();
        var registry = new ReportSectionRegistry([builder]);
        var source = Source();
        var overhead = ReportSnapshotBuilder.StoredBytes(ReportSnapshotBuilder.Build(source, registry, CancellationToken.None).CanonicalPayload);
        builder.Text = new string('x', (int)(byteCount - overhead));

        return ReportSnapshotBuilder.Build(source, registry, CancellationToken.None);
    }

    private static ReportSource Source()
    {
        var source = ReportSectionFixture.Source();
        return source with { Request = source.Request with { Sections = ["traceability"] } };
    }

    private static JsonElement Body(object ids, string[]? sections = null) => JsonSerializer.SerializeToElement(new
    {
        scope_kind = ReportScopeKinds.FeatureNodes,
        selected_ids = ids,
        sections = sections ?? ["traceability"],
        language = "en",
    });

    private sealed class SizedSectionBuilder : IReportSectionBuilder
    {
        public string SectionId => "traceability";

        internal string Text { get; set; } = string.Empty;

        internal int Calls { get; private set; }

        public JsonNode Build(ReportSource source)
        {
            Calls++;
            return new JsonObject { ["text"] = Text };
        }
    }
}
