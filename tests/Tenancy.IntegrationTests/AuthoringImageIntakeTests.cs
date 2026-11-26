using System.Net;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class AuthoringImageIntakeTests(PostgresTenancyFixture fixture)
{
    [Fact]
    public async Task StoresArtifact_ZeroSends()
    {
        var provider = new RecordingProvider();
        await using var world = await IntakeApiWorld.StartAsync(fixture, services => services.AddSingleton<IHealingLlmProvider>(provider));
        var artifact = await world.ArtifactAsync("requirement_image", "image/webp", 100);
        var created = await world.PostAsync(new { kind = "image", artifact_id = artifact });
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        created.Text("state").ShouldBe("ready");
        var id = created.Id("intake_id");
        var status = await world.Client.SendAsync(HttpMethod.Get, $"{world.Path}/{id}", world.As);
        status.Id("artifact_id").ShouldBe(artifact);
        (await fixture.ScalarAsync<bool>(world.Database, $"""
            select artifact_id='{artifact}' and input_text is null and extracted_text is null and extracted_text_hash is null
            from public.authoring_intake where id='{id}'
            """)).ShouldBeTrue();
        provider.Calls.ShouldBe(0);
    }

    private sealed class RecordingProvider : IHealingLlmProvider
    {
        internal int Calls { get; private set; }

        public Task<HealingLlmResult> InvokeAsync(HealingLlmRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("An image reference must never reach a provider.");
        }
    }
}
