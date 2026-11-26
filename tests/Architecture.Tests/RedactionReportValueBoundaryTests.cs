using System.Text;
using System.Text.Json;
using Evidgate.Modules.Healing.Application;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

public sealed class RedactionReportValueBoundaryTests
{
    private static readonly string Password = string.Concat("report", "-private-value");
    private static readonly RedactionPipeline Pipeline = new();

    public static TheoryData<string> SecretShapedSourceIds => new()
    {
        string.Concat("sk-", "R1t2Y3u4I5o6P7a8S9d0F1g2"),
        string.Concat("0912", "345678"),
        string.Concat("password:", "report-private-value"),
    };

    [Theory]
    [MemberData(nameof(SecretShapedSourceIds))]
    public void Redact_SecretShapedSourceId_DoesNotCopyTheValueIntoTheReport(string sourceId)
    {
        var control = Pipeline.Redact(new PromptContextBuilder()
            .Build([new PromptSegment(PromptSegmentKind.Log, "record-1", "public step")]).Payload);
        control.Blocked.ShouldBeFalse();
        control.Report.Findings.ShouldBeEmpty();

        var payload = new PromptContextBuilder()
            .Build([new PromptSegment(PromptSegmentKind.Log, sourceId, "public step")]).Payload;
        var outcome = Pipeline.Redact(payload);

        outcome.Report.Findings.ShouldNotBeEmpty();
        outcome.Report.ToJson().ShouldNotContain(sourceId);
        outcome.Report.ToString().ShouldNotContain(sourceId);
        outcome.ToString().ShouldNotContain(sourceId);
        foreach (var finding in outcome.Report.Findings)
        {
            finding.FieldPath.ShouldNotContain(sourceId);
            finding.FieldPath.ShouldBe("segment:1");
            finding.ToString().ShouldNotContain(sourceId);
        }
    }

    [Fact]
    public void Redact_EncodedResidual_ReportAndStringFormsNeverHoldEitherValue()
    {
        var benign = Convert.ToBase64String(Encoding.UTF8.GetBytes("document=" + Password));
        var control = Pipeline.Redact(benign);
        control.Blocked.ShouldBeFalse();
        control.Text.ShouldBe(benign);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("password=" + Password));

        var outcome = Pipeline.Redact(encoded);

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        foreach (var rendered in new[] { outcome.Report.ToJson(), outcome.Report.ToString(), outcome.ToString() })
        {
            rendered.ShouldNotContain(Password);
            rendered.ShouldNotContain(encoded);
        }

        foreach (var finding in outcome.Report.Findings)
        {
            finding.ToString().ShouldNotContain(Password);
            finding.ToString().ShouldNotContain(encoded);
        }
    }

    [Fact]
    public void Redact_MaskedCredential_ReportContainsOnlyItsDocumentedMembers()
    {
        var control = Pipeline.Redact("document=" + Password);
        control.Blocked.ShouldBeFalse();
        control.Report.Findings.ShouldBeEmpty();
        var outcome = Pipeline.Redact("password=" + Password);
        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldNotContain(Password);
        using var json = JsonDocument.Parse(outcome.Report.ToJson());

        var root = json.RootElement;
        root.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal)
            .ShouldBe(["findings", "rule_version"]);
        root.GetProperty("rule_version").GetString().ShouldBe(RedactionRules.Version);
        var findings = root.GetProperty("findings").EnumerateArray().ToArray();
        findings.ShouldNotBeEmpty();
        foreach (var finding in findings)
        {
            finding.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal)
                .ShouldBe(["count", "field_path", "rule_id"]);
            finding.GetProperty("count").GetInt32().ShouldBeGreaterThan(0);
            finding.GetProperty("field_path").GetString().ShouldBe("payload");
            finding.GetProperty("rule_id").GetString().ShouldBe("credential_assignment");
        }

        outcome.Report.ToJson().ShouldNotContain(Password);
        outcome.Report.ToString().ShouldNotContain(Password);
        outcome.ToString().ShouldNotContain(Password);
    }

    [Fact]
    public void Redact_InvalidSourceId_UsesOnlyTheSegmentOrdinal()
    {
        var control = Pipeline.Redact("<<<EVG-DATA kind=log id=record-1>>>\npassword=" + Password);
        control.Report.Findings.ShouldNotBeEmpty();
        control.Report.Findings.All(finding => finding.FieldPath == "segment:1").ShouldBeTrue();
        var payload = "<<<EVG-DATA kind=log id=invalid " + Password + ">>>\npassword=" + Password;

        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBeFalse();
        outcome.Report.Findings.ShouldNotBeEmpty();
        outcome.Report.Findings.All(finding => finding.FieldPath == "segment:1").ShouldBeTrue();
        outcome.Report.ToJson().ShouldNotContain(Password);
    }

    [Fact]
    public void Redact_MultipleSegments_SortsFindingsWithoutCopyingContent()
    {
        var email = string.Concat("private.person", "@example.test");
        var payload = new PromptContextBuilder().Build(
        [
            new PromptSegment(PromptSegmentKind.Log, "beta", "contact " + email),
            new PromptSegment(PromptSegmentKind.Log, "alpha", "contact " + email),
        ]).Payload;

        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBeFalse();
        outcome.Report.Findings.ShouldBe(
        [
            new RedactionFinding("email", "segment:1", 1),
            new RedactionFinding("email", "segment:2", 1),
        ]);
        outcome.Report.ToJson().ShouldNotContain(email);
        outcome.Report.ToJson().ShouldNotContain("private.person");
        RedactionReport.Matches(outcome.Report.ToJson(), outcome.Report.Hash).ShouldBeTrue();
    }
}
