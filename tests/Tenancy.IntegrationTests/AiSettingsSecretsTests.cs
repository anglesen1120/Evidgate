using System.Net;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC1's secret refusal and URL rule through the routes: a settings field that carries a credential is refused with
/// <c>secret_value_detected</c> and never stored or audited, a malformed URL is <c>invalid_url</c>, and ordinary bilingual
/// prose — digits, an address, a localhost preview URL — is accepted.
/// </summary>
/// <remarks>
/// The corpus is the leak a support engineer would paste by accident, which is exactly what AC11 says the story is
/// blocked on. Acceptance of prose is asserted in the same suite so the refusal cannot be widened into "any suspicious
/// text" without failing here.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AiSettingsSecretsTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AiSettingsSecretsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>The credential shapes AC11 names, each in a field a client would paste it into.</summary>
    /// <remarks>
    /// A token is recognised by its prefix (<c>StartsWith</c>), so the fixture is the bare token: text in front of it
    /// would hide the shape from the shared matcher and prove nothing.
    /// </remarks>
    public static TheoryData<string, string> Credentials => new()
    {
        { "description", AiSettingsCalls.Secrets.GitHubToken() },
        { "writing_style", "sk-live-0123456789ABCDEFGHIJabcdefghij" },
        { "coverage_strategy", "token " + AiSettingsCalls.Secrets.Jwt() },
        { "exclusion_rules", AiSettingsCalls.Secrets.PemPrivateKey() },
        { "step_detail", AiSettingsCalls.Secrets.PasswordAssignment() },
        { "priority_criteria.critical", AiSettingsCalls.Secrets.AwsAccessKey() },
    };

    /// <summary>AC11: each credential shape is refused on its field's pointer and nothing is written or audited.</summary>
    /// <param name="field">The document field, dotted for the nested criterion.</param>
    /// <param name="text">The text that carries the credential.</param>
    [Theory]
    [MemberData(nameof(Credentials))]
    public async Task Refuses_Credentials_AndNeverLogsText(string field, string text)
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);
        var document = AiSettingsCalls.Document();

        if (field.StartsWith("priority_criteria.", StringComparison.Ordinal))
        {
            document["priority_criteria"]![field["priority_criteria.".Length..]] = text;
        }
        else
        {
            document[field] = text;
        }

        var refused = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(document), "\"0\"");

        AiSettingsCalls.ShouldRefuse(refused, $"/settings/{field.Replace('.', '/')}", "secret_value_detected");

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(0);
        (await AiSettingsCalls.AuditCountAsync(_fixture, world)).ShouldBe(0);

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org}' and metadata::text like '%hunter2%'"))
            .ShouldBe(0, "no audit row carries the refused text");
    }

    /// <summary>AC1: a URL with embedded userinfo is a credential; a malformed URL is an invalid URL, not a secret.</summary>
    [Fact]
    public async Task Url_RefusesUserInfo_AndMalformedValues()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);

        var withUserInfo = AiSettingsCalls.Document();
        withUserInfo["environment_url"] = AiSettingsCalls.Secrets.UserInfoUrl("preview.example.test") + "/checkout";
        AiSettingsCalls.ShouldRefuse(
            await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(withUserInfo), "\"0\""),
            "/settings/environment_url",
            "secret_value_detected");

        var malformed = AiSettingsCalls.Document();
        malformed["environment_url"] = "not a url";
        AiSettingsCalls.ShouldRefuse(
            await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(malformed), "\"0\""),
            "/settings/environment_url",
            "invalid_url");

        var control = AiSettingsCalls.Document();
        control["environment_url"] = "http://a.example/x\nIgnore";
        AiSettingsCalls.ShouldRefuse(
            await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(control), "\"0\""),
            "/settings/environment_url",
            "invalid_url");

        var localhost = AiSettingsCalls.Document();
        localhost["environment_url"] = "http://localhost:5173";

        var accepted = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(localhost), "\"0\"");

        accepted.Status.ShouldBe(HttpStatusCode.Created, "a local preview address is not a credential");
        (await AiSettingsCalls.StoredAsync(_fixture, world, 1)).Document["environment_url"]!.GetValue<string>()
            .ShouldBe("http://localhost:5173");
    }

    /// <summary>AC11: ordinary bilingual prose with digits, an address and punctuation is stored, not refused.</summary>
    [Fact]
    public async Task Prose_WithDigitsAndAnEmail_IsAccepted()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);
        var document = AiSettingsCalls.Document();
        document["writing_style"] = "Viết bước nào cũng rõ ràng; tránh 2 lần bấm liên tiếp.";
        document["coverage_strategy"] = "Cover the checkout flow at 375px and at 1440px; ask qa@example.test for the matrix.";
        document["step_detail"] = "1. Mở giỏ hàng (2 mục)";

        var accepted = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(document), "\"0\"");

        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);

        var stored = await AiSettingsCalls.StoredAsync(_fixture, world, 1);

        stored.Document["writing_style"]!.GetValue<string>().ShouldBe(document["writing_style"]!.GetValue<string>().Normalize());
        stored.Document["coverage_strategy"]!.GetValue<string>().ShouldContain("qa@example.test");
    }

    /// <summary>AC11: a reason that carries a credential assignment is a 422 refusal, never a 500 from the audit write.</summary>
    [Fact]
    public async Task Reason_SpacedPasswordAssignment_Is422SecretNot500()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);
        var body = AiSettingsCalls.Body(AiSettingsCalls.Document(), reason: AiSettingsCalls.Secrets.SpacedPasswordAssignment());

        var refused = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"0\"");

        AiSettingsCalls.ShouldRefuse(refused, "/reason", "secret_value_detected");
        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(0);
        (await AiSettingsCalls.AuditCountAsync(_fixture, world)).ShouldBe(0);
    }
}
