using System.Diagnostics;
using System.Text;
using Evidgate.Modules.Healing.Application;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// The egress masking step: a payload that leaves for an AI provider carries none of the secret-shaped or personal
/// values it was built from, the pass that follows refuses what the first one missed in an encoded form, and the report
/// says what was done without holding a single value. Pure functions; no database.
/// </summary>
public sealed class RedactionPipelineTests
{
    // Built at runtime so no scanner sees a literal credential in the source.
    private static readonly string Password = "hunter2" + "hunter2";
    private static readonly string BearerToken = "abcdef0123456789" + "abcdef0123456789";
    private static readonly string ApiKey = "sk-" + "A1b2C3d4E5f6G7h8I9j0K1l2";
    private static readonly string CardNumber = "4111 1111 " + "1111 1111";

    private static readonly RedactionPipeline Pipeline = new();

    public static TheoryData<string, string, string> Secrets => new()
    {
        { "password", "password=" + Password, "credential_assignment" },
        { "cookie", "Cookie: session=" + "abc123def456ghi789", "cookie|credential_assignment" },
        { "authorization", "Authorization: Bearer " + BearerToken, "authorization_header" },
        { "bearer in prose", "the token is Bearer " + BearerToken + " ok", "bearer_token" },
        { "email", "contact jane.doe" + "@example.com now", "email" },
        { "vn phone", "call 0912 345 " + "678 now", "phone" },
        { "international phone", "call +84 912 345 " + "678 now", "phone" },
        { "api key", "key " + ApiKey + " end", "api_key_shape" },
        { "private key", "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\n-----END PRIVATE KEY-----", "private_key" },
        { "dom input value", "<input id=\"card\" value=\"" + CardNumber + "\">", "input_value" },
        { "playwright fill", "await page.fill('#card', '" + CardNumber + "');", "input_value" },
    };

    [Theory]
    [MemberData(nameof(Secrets))]
    public void Redact_MasksEveryClassOfSecretWithoutLeavingTheValue(string label, string payload, string ruleId)
    {
        var outcome = Pipeline.Redact("before\n" + payload + "\nafter");

        outcome.Blocked.ShouldBeFalse(label);
        outcome.Report.Findings.ShouldContain(f => ruleId.Split('|').Contains(f.RuleId), label);
        foreach (var value in new[] { Password, BearerToken, ApiKey, CardNumber, "jane.doe", "0912 345", "+84 912", "abc123def456ghi789", "MIIEvQIBADANBg" })
        {
            if (payload.Contains(value, StringComparison.Ordinal))
            {
                outcome.Text.ShouldNotContain(value, customMessage: label);
            }
        }

        outcome.Text.ShouldStartWith("before\n");
        outcome.Text.ShouldEndWith("\nafter");
    }

    [Fact]
    public void Redact_LeavesBenignTextByteIdentical()
    {
        const string benign = "Step 3 failed: expected '#checkout' to be visible after 30000ms\nat tests/cart.spec.ts:42\n";

        var outcome = Pipeline.Redact(benign);

        outcome.Text.ShouldBe(benign);
        outcome.Blocked.ShouldBeFalse();
        outcome.Report.Findings.ShouldBeEmpty();
    }

    [Fact]
    public void Redact_IsDeterministic()
    {
        var payload = "a jane.doe" + "@example.com b password=" + Password + " c";

        var first = Pipeline.Redact(payload);
        var second = new RedactionPipeline().Redact(payload);

        second.Text.ShouldBe(first.Text);
        second.Report.Hash.ShouldBe(first.Report.Hash);
    }

    [Fact]
    public void Redact_ReportNamesRuleAndFieldAndCountButNeverTheValue()
    {
        var payload = new PromptContextBuilder()
            .Build([new PromptSegment(PromptSegmentKind.Log, "rec-1", "mail jane.doe" + "@example.com and bob@example.org")])
            .Payload;

        var outcome = Pipeline.Redact(payload);

        outcome.Report.Findings.ShouldHaveSingleItem().ShouldBe(new RedactionFinding("email", "segment:rec-1", 2));
        var json = outcome.Report.ToJson();
        json.ShouldNotContain("jane");
        json.ShouldNotContain("example");
        outcome.Report.RuleVersion.ShouldBe(RedactionRules.Version);
    }

    [Fact]
    public void Redact_Base64EncodedSecret_IsBlockedNotSent()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("password=" + Password));

        var outcome = Pipeline.Redact("note " + encoded + " end");

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(f => f.RuleId == "residual_base64");
    }

    [Fact]
    public void Redact_PercentEncodedSecret_IsBlockedNotSent()
    {
        var outcome = Pipeline.Redact("url ?password%3D" + Password + " end");

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(f => f.RuleId == "residual_urlencoded");
    }

    [Fact]
    public void Redact_UnicodeEscapedSecret_IsBlockedNotSent()
    {
        var outcome = Pipeline.Redact("json \\u0070assword=" + Password + " end");

        outcome.Blocked.ShouldBeTrue();
        outcome.Text.ShouldBeEmpty();
        outcome.Report.Findings.ShouldContain(f => f.RuleId == "residual_unicode_escape");
    }

    [Fact]
    public void Redact_OneMegabytePayload_FinishesInsideTheBudgetUnblocked()
    {
        var line = "GET /api/cart 200 12ms jane.doe" + "@example.com password=" + Password + "\n";
        var text = new StringBuilder();
        while (text.Length < 1_000_000)
        {
            text.Append(line);
        }

        var clock = Stopwatch.StartNew();
        var outcome = Pipeline.Redact(text.ToString());
        clock.Stop();

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldNotContain(Password);
        outcome.Text.ShouldNotContain("jane.doe");
        clock.Elapsed.ShouldBeLessThan(RedactionPipeline.Budget);
    }

    [Fact]
    public void Redact_SecretAcrossAWindowBoundary_StaysMaskedWhenTheLineIsShorterThanTheWindow()
    {
        var filler = new string('x', RedactionPipeline.MaxWindowChars - 20);
        var payload = filler + "\npassword=" + Password + "\n" + filler;

        var outcome = Pipeline.Redact(payload);

        outcome.Blocked.ShouldBeFalse();
        outcome.Text.ShouldNotContain(Password);
    }

    [Fact]
    public void Redact_EmptyPayload_IsEmptyAndNotBlocked()
    {
        var outcome = Pipeline.Redact(string.Empty);

        outcome.Text.ShouldBeEmpty();
        outcome.Blocked.ShouldBeFalse();
    }
}

/// <summary>The report hash is what a call row stores: stable for the same findings, sensitive to any change.</summary>
public sealed class RedactionReportHashTests
{
    private static RedactionReport Report(params RedactionFinding[] findings) => new("evg-redact-v1", findings);

    [Fact]
    public void Hash_SameFindings_SameHash()
    {
        var a = Report(new RedactionFinding("email", "segment:a", 2));
        var b = Report(new RedactionFinding("email", "segment:a", 2));

        a.Hash.ShouldBe(b.Hash);
        a.Hash.ShouldStartWith("sha256:");
    }

    [Fact]
    public void Matches_IgnoresMemberOrderOfTheStoredJson()
    {
        var report = Report(new RedactionFinding("email", "segment:a", 2));
        var reordered =
            "{\"findings\":[{\"rule_id\":\"email\",\"field_path\":\"segment:a\",\"count\":2}],\"rule_version\":\"evg-redact-v1\"}";

        RedactionReport.Matches(reordered, report.Hash).ShouldBeTrue();
    }

    [Fact]
    public void Matches_TamperedReport_IsRefused()
    {
        var report = Report(new RedactionFinding("email", "segment:a", 2));
        var tampered = Report(new RedactionFinding("email", "segment:a", 1)).ToJson();

        RedactionReport.Matches(tampered, report.Hash).ShouldBeFalse();
    }
}
