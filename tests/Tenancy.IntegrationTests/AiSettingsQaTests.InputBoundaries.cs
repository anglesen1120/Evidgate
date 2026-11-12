using System.Net;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class AiSettingsInputBoundaryTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) : IClassFixture<TenancyHostPool>
{
    public static TheoryData<string, int> TextFields => new()
    {
        { "/settings/description", 1000 },
        { "/settings/environment_url", 2048 },
        { "/settings/writing_style", 250 },
        { "/settings/coverage_strategy", 1500 },
        { "/settings/priority_criteria/critical", 250 },
        { "/settings/priority_criteria/high", 250 },
        { "/settings/priority_criteria/medium", 250 },
        { "/settings/priority_criteria/low", 250 },
        { "/settings/exclusion_rules", 1500 },
        { "/settings/step_detail", 200 },
        { "/reason", 500 },
    };

    [Theory]
    [MemberData(nameof(TextFields))]
    public async Task TextField_LimitMinusOneAndLimitAccepted_OneOverRefused(string path, int limit)
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture, hosts);
        var rejected = BodyWith(path, ValueOfLength(path, limit + 1));
        var idempotencyBefore = await AiSettingsCalls.IdempotencyCountAsync(fixture, world);

        var answer = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, rejected, "\"0\"");

        AiSettingsCalls.ShouldRefuse(answer, path, "too_long");
        (await AiSettingsCalls.SettingsCountAsync(fixture, world)).ShouldBe(0);
        (await AiSettingsCalls.AuditCountAsync(fixture, world)).ShouldBe(0);
        (await AiSettingsCalls.IdempotencyCountAsync(fixture, world)).ShouldBe(idempotencyBefore);

        for (var delta = -1; delta <= 0; delta++)
        {
            var version = delta + 2;
            var value = ValueOfLength(path, limit + delta);
            var accepted = await AiSettingsCalls.SaveAsync(
                world.Client, world.As, world, BodyWith(path, value), $"\"{version - 1}\"");

            accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
            var stored = await AiSettingsCalls.StoredAsync(fixture, world, version);
            var actual = path == "/reason" ? stored.Reason : At(stored.Document, path["/settings".Length..]).GetValue<string>();
            actual.ShouldBe(value);
        }
    }

    [Theory]
    [MemberData(nameof(TextFields))]
    public async Task TextField_SecretShapesRefused_OrdinaryTextAccepted(string path, int limit)
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture, hosts);
        var idempotencyBefore = await AiSettingsCalls.IdempotencyCountAsync(fixture, world);
        string[] secrets =
        [
            AiSettingsCalls.Secrets.GitHubToken(),
            "sk-live-0123456789ABCDEFGHIJabcdefghij",
            AiSettingsCalls.Secrets.PasswordAssignment(),
            "Bearer example-test-credential",
            "-----BEGIN" + " PRIVATE KEY-----",
            "0123456789abcdef0123456789abcdef",
            AiSettingsCalls.Secrets.Jwt(),
        ];

        foreach (var secret in secrets)
        {
            var value = path == "/settings/environment_url"
                ? "https://example.test/#" + Uri.EscapeDataString(secret)
                : secret;
            value.Length.ShouldBeLessThan(limit, "the refusal must be secret detection, not the field cap");
            var refused = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, BodyWith(path, value), "\"0\"");

            AiSettingsCalls.ShouldRefuse(refused, path, "secret_value_detected");
            refused.Body.GetRawText().ShouldNotContain(secret);
        }

        (await AiSettingsCalls.SettingsCountAsync(fixture, world)).ShouldBe(0);
        (await AiSettingsCalls.AuditCountAsync(fixture, world)).ShouldBe(0);
        (await AiSettingsCalls.IdempotencyCountAsync(fixture, world)).ShouldBe(idempotencyBefore);

        var ordinary = path == "/settings/environment_url"
            ? "https://example.test/checkout#details"
            : "Kiểm tra 2 bước; ask qa@example.test.";
        var accepted = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, BodyWith(path, ordinary), "\"0\"");
        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
    }

    [Theory]
    [InlineData("file:///etc/passwd", "invalid_url")]
    [InlineData("javascript:alert(1)", "invalid_url")]
    [InlineData("//example.test/checkout", "invalid_url")]
    [InlineData("https://" + "user" + ":" + "pw" + "@example.test/checkout", "secret_value_detected")]
    [InlineData("https://example.test/#password%3Dhunter2", "secret_value_detected")]
    [InlineData("https://example.test/password%3Dhunter2", "secret_value_detected")]
    [InlineData("https://example.test/?password=hunter2", "secret_value_detected")]
    public async Task EnvironmentUrl_UnsafeShapeRefused_CredentialFreeHttpsAccepted(string value, string reason)
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture, hosts);
        var refused = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, BodyWith("/settings/environment_url", value), "\"0\"");

        AiSettingsCalls.ShouldRefuse(refused, "/settings/environment_url", reason);
        (await AiSettingsCalls.SettingsCountAsync(fixture, world)).ShouldBe(0);
        (await AiSettingsCalls.AuditCountAsync(fixture, world)).ShouldBe(0);

        var accepted = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, BodyWith("/settings/environment_url", "https://example.test/checkout"), "\"0\"");
        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
    }

    [Theory]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://[::1]")]
    [InlineData("http://localhost:5173")]
    [InlineData("http://127.0.0.1")]
    public async Task EnvironmentUrl_LocalAndMetadataAddresses_AreStoredAsGuidance(string value)
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture, hosts);
        var accepted = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, BodyWith("/settings/environment_url", value), "\"0\"");

        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
        var stored = await AiSettingsCalls.StoredAsync(fixture, world, 1);
        stored.Document["environment_url"]?.GetValue<string>().ShouldBe(value);
    }

    private static JsonObject BodyWith(string path, string value)
    {
        var document = AiSettingsCalls.Document();
        document["description"] = "Guidance";
        var body = AiSettingsCalls.Body(document);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        JsonNode current = body;
        foreach (var segment in segments[..^1])
        {
            current = current[segment] ?? throw new InvalidOperationException("missing input parent");
        }

        current[segments[^1]] = value;
        if (path == "/reason")
        {
            body["settings"]!["description"] = $"Guidance with reason length {value.Length}";
        }

        return body;
    }

    private static JsonNode At(JsonObject document, string path)
    {
        JsonNode current = document;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current[segment] ?? throw new InvalidOperationException("missing stored field");
        }

        return current;
    }

    private static string ValueOfLength(string path, int length)
    {
        const string prefix = "https://example.test/";
        return path == "/settings/environment_url"
            ? prefix + new string('x', length - prefix.Length)
            : AiSettingsCalls.ProseOfLength(length);
    }
}
