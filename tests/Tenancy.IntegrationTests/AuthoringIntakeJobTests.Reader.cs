using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Application.Intake;
using Evidgate.Modules.Evidence.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringIntakeJobTests
{
    [Fact]
    public async Task ReaderReturnsAReadyArtifactOfTheProjectAndNoOneElse()
    {
        var world = await SeedWorldAsync();
        var other = await SeedWorldAsync();
        var intake = await SeedDocumentAsync(world, AuthoringExtractionFixtures.Docx("Readable"));
        var artifact = await ColumnAsync<Guid>(world, intake, "artifact_id");
        await using var api = TenancyApiFactory.GrantAll(fixture.AppConnectionString(world.Database));
        var reader = api.Services.GetRequiredService<IIntakeArtifactReader>();
        var scopes = api.Services.GetRequiredService<ITenantScopeFactory>();

        await using (await scopes.BeginAsync(world.Org, readOnlyTransaction: true, cancellationToken: Token))
        {
            var found = (await reader.ReadEligibleAsync(world.Org, world.Project, artifact, Token)).ShouldNotBeNull();
            found.Kind.ShouldBe(ArtifactKinds.RequirementDocument);
            found.MediaType.ShouldBe(DocxType);
            found.MediaAllowed.ShouldBeTrue();
            found.MaxBytes.ShouldBe(IntakeCaps.Default.MaxDocumentBytes);

            // Another project of the same tenant, and an unknown id, read as one answer: absent.
            (await reader.ReadEligibleAsync(world.Org, Guid.CreateVersion7(), artifact, Token)).ShouldBeNull();
            (await reader.ReadEligibleAsync(world.Org, world.Project, Guid.CreateVersion7(), Token)).ShouldBeNull();
        }

        // Another tenant's scope cannot see the row at all.
        await using (await scopes.BeginAsync(other.Org, readOnlyTransaction: true, cancellationToken: Token))
        {
            (await reader.ReadEligibleAsync(other.Org, other.Project, artifact, Token)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task ReaderReportsAnArtifactThePolicyForbidsAsNotAllowed_AndAnUnreadyOneAsAbsent()
    {
        var world = await SeedWorldAsync();
        var intake = await SeedScenarioAsync(world, AuthoringExtractionFixtures.Docx("Policy"), "policy_off");
        var artifact = await ColumnAsync<Guid>(world, intake, "artifact_id");
        await using var api = TenancyApiFactory.GrantAll(fixture.AppConnectionString(world.Database));
        var reader = api.Services.GetRequiredService<IIntakeArtifactReader>();
        var scopes = api.Services.GetRequiredService<ITenantScopeFactory>();

        await using (await scopes.BeginAsync(world.Org, readOnlyTransaction: true, cancellationToken: Token))
        {
            var found = (await reader.ReadEligibleAsync(world.Org, world.Project, artifact, Token)).ShouldNotBeNull();
            found.MediaAllowed.ShouldBeFalse();
        }

        await fixture.ExecuteAsync(
            world.Database,
            $"update public.artifact set state = 'pending_upload' where id = '{artifact:D}'");

        await using (await scopes.BeginAsync(world.Org, readOnlyTransaction: true, cancellationToken: Token))
        {
            (await reader.ReadEligibleAsync(world.Org, world.Project, artifact, Token)).ShouldBeNull();
        }
    }
}
