using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC1's caps against a real PostgreSQL: each limit is inclusive (a value of exactly the cap is stored) and one code
/// point over is refused with 422 <c>too_long</c> on the field's own pointer, leaving no row behind.
/// </summary>
/// <remarks>
/// The four contractual caps of 13.2.1 §3/§4 and the three OI-13-222 defaults are checked on their JSON pointer, because
/// the pointer is what a client binds its error message to (AC12). The unit is the code point, which the surrogate-pair
/// case in <see cref="AiSettingsTextTests"/> pins separately.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AiSettingsLimitTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;

    public AiSettingsLimitTests(PostgresTenancyFixture fixture, TenancyHostPool hosts)
    {
        _fixture = fixture;
        _hosts = hosts;
    }

    /// <summary>Each contractual cap, with the column that stores its field.</summary>
    public static TheoryData<string, int> Boundaries => new()
    {
        { "writing_style", AiSettingsCalls.WritingStyleLimit },
        { "coverage_strategy", AiSettingsCalls.CoverageLimit },
        { "exclusion_rules", AiSettingsCalls.ExclusionsLimit },
        { "step_detail", AiSettingsCalls.StepDetailLimit },
    };

    /// <summary>AC1: the cap is inclusive and the field one over it is refused, with nothing written.</summary>
    /// <param name="field">The document member, which is also its column.</param>
    /// <param name="limit">The cap in code points.</param>
    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task FieldCap_IsInclusive_AndRefusesOneOver(string field, int limit)
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);

        var over = AiSettingsCalls.Document();
        over[field] = AiSettingsCalls.ProseOfLength(limit + 1);

        var refused = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(over), "\"0\"");

        AiSettingsCalls.ShouldRefuse(refused, $"/settings/{field}", "too_long");
        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(0, "a refused save stores nothing");

        var atLimit = AiSettingsCalls.Document();
        atLimit[field] = AiSettingsCalls.ProseOfLength(limit);

        var accepted = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(atLimit), "\"0\"");

        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);

        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select length({field}) from public.project_ai_settings where org_id = '{world.Org}'"))
            .ShouldBe(limit, "the value of exactly the cap is stored whole");
    }

    /// <summary>AC1/OI-13-222: the description cap is 1000 code points, inclusive.</summary>
    [Fact]
    public async Task DescriptionCap_IsInclusive()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);

        var over = AiSettingsCalls.Document();
        over["description"] = AiSettingsCalls.ProseOfLength(AiSettingsCalls.DescriptionLimit + 1);

        AiSettingsCalls.ShouldRefuse(
            await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(over), "\"0\""),
            "/settings/description",
            "too_long");

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(0);

        var atLimit = AiSettingsCalls.Document();
        atLimit["description"] = AiSettingsCalls.ProseOfLength(AiSettingsCalls.DescriptionLimit);

        (await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(atLimit), "\"0\""))
            .Status.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>AC1/OI-13-222: every priority criterion is capped at 250 code points, inclusive, on its nested pointer.</summary>
    [Fact]
    public async Task CriterionCaps_AreInclusive_OnTheNestedPointer()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);

        foreach (var criterion in new[] { "critical", "high", "medium", "low" })
        {
            var over = AiSettingsCalls.Document();
            over["priority_criteria"]![criterion] = AiSettingsCalls.ProseOfLength(AiSettingsCalls.CriterionLimit + 1);

            AiSettingsCalls.ShouldRefuse(
                await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(over), "\"0\""),
                $"/settings/priority_criteria/{criterion}",
                "too_long");
        }

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(0);

        var atLimit = AiSettingsCalls.Document();
        atLimit["priority_criteria"]!["critical"] = AiSettingsCalls.ProseOfLength(AiSettingsCalls.CriterionLimit);

        (await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(atLimit), "\"0\""))
            .Status.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>AC1/OI-13-222: the environment URL cap is 2048 code points, inclusive.</summary>
    [Fact]
    public async Task EnvironmentUrlCap_IsInclusive()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);
        const string Prefix = "https://example.test/";

        var over = AiSettingsCalls.Document();
        over["environment_url"] = Prefix + new string('x', AiSettingsCalls.UrlLimit + 1 - Prefix.Length);

        AiSettingsCalls.ShouldRefuse(
            await AiSettingsCalls.SaveAsync(world.Client, world.As, world, AiSettingsCalls.Body(over), "\"0\""),
            "/settings/environment_url",
            "too_long");

        var atLimit = AiSettingsCalls.Document();
        atLimit["environment_url"] = Prefix + new string('x', AiSettingsCalls.UrlLimit - Prefix.Length);

        var accepted = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(atLimit), "\"0\"");

        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
        AiSettingsCalls.SettingsOf(accepted)["environment_url"]!.GetValue<string>()[0..Prefix.Length].ShouldBe(Prefix);
    }

    /// <summary>
    /// AC1/OI-13-222 P8: a body whose declared length is over the request-body limit is refused with 413
    /// <c>payload_too_large</c> before it is deserialised, and stores nothing.
    /// </summary>
    [Fact]
    public async Task PayloadTooLarge_Is413()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);

        var document = AiSettingsCalls.Document();
        document["description"] = new string('a', 2 * 1024 * 1024);
        var oversized = AiSettingsCalls.Body(document).ToJsonString();

        using var request = new HttpRequestMessage(HttpMethod.Put, world.Settings);
        world.As(request.Headers);
        request.Headers.Add("Idempotency-Key", RegistryApi.NewKey());
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        request.Content = new StringContent(oversized, Encoding.UTF8, "application/json");

        using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge, text[..Math.Min(text.Length, 200)]);
        text.ShouldContain("payload_too_large");

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(0);
        (await AiSettingsCalls.AuditCountAsync(_fixture, world)).ShouldBe(0);
    }
}
