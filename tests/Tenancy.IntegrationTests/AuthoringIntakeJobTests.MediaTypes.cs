using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Application.Intake;
using Evidgate.Modules.Evidence.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringIntakeJobTests
{
    private const string PdfType = "application/pdf";

    /// <summary>
    /// The upload accepts <c>APPLICATION/PDF</c> and a type with parameters, so the reader, the planner and the job must
    /// read them as the PDF type too, and the row must store the canonical lowercase type.
    /// </summary>
    [Theory]
    [InlineData("application/pdf")]
    [InlineData("APPLICATION/PDF")]
    [InlineData("application/pdf; charset=binary")]
    public async Task MediaType_SpellingsTheUploadAccepts_AreReadPlannedAndExtractedAsOneType(string artifactMedia)
    {
        var world = await SeedWorldAsync();
        var pdf = AuthoringExtractionFixtures.Pdf(["Spelled differently"]);
        var intake = await SeedIntakeAsync(world, pdf.LongLength, IntakeCapsJson.Serialize(IntakeCaps.Default), "pdf", PdfType, artifactMedia);
        var artifact = await ColumnAsync<Guid>(world, intake, "artifact_id");
        await using var api = TenancyApiFactory.GrantAll(fixture.AppConnectionString(world.Database));
        var scopes = api.Services.GetRequiredService<ITenantScopeFactory>();
        var reader = api.Services.GetRequiredService<IIntakeArtifactReader>();

        await using (await scopes.BeginAsync(world.Org, readOnlyTransaction: true, cancellationToken: Token))
        {
            (await reader.ReadEligibleAsync(world.Org, world.Project, artifact, Token)).ShouldNotBeNull().MediaAllowed.ShouldBeTrue();
        }

        // The planner stores the accepted type in its own spelling: the intake row's own check needs it lowercase.
        await fixture.ExecuteAsync(world.Database, $"delete from public.authoring_intake where id = '{intake:D}'");
        var body = JsonSerializer.SerializeToElement(new { kind = "pdf", feature_node_id = world.Node, artifact_id = artifact });
        await using (await scopes.BeginAsync(world.Org, readOnlyTransaction: true, cancellationToken: Token))
        {
            using var di = api.Services.CreateScope();
            var (plan, error) = await di.ServiceProvider.GetRequiredService<IntakePlanner>()
                .PlanAsync(world.Org, world.Project, body, IntakeCaps.Default, Token);
            error.ShouldBeNull();
            plan.ShouldNotBeNull().MediaType.ShouldBe(PdfType);
        }

        var second = await SeedIntakeAsync(world, pdf.LongLength, IntakeCapsJson.Serialize(IntakeCaps.Default), "pdf", PdfType, artifactMedia);
        var job = CreateJob(api.Services, (_, _, _) => Task.FromResult<byte[]?>(pdf));

        await job.RunAsync(second, world.Org, Token);

        (await StateAsync(world, second)).ShouldBe("ready");
        (await ColumnAsync<string>(world, second, "extracted_text")).ShouldContain("Spelled differently");
    }

    /// <summary>The same comparison refuses what the upload refuses: nothing is trimmed into a match.</summary>
    [Theory]
    [InlineData("application/pdf\n")]
    [InlineData(" application/pdf")]
    [InlineData("application/pdfx")]
    public async Task MediaType_SpellingsTheUploadRefuses_AreNotAllowedByTheReader(string artifactMedia)
    {
        var world = await SeedWorldAsync();
        var intake = await SeedIntakeAsync(world, 10, IntakeCapsJson.Serialize(IntakeCaps.Default), "pdf", PdfType, artifactMedia);
        var artifact = await ColumnAsync<Guid>(world, intake, "artifact_id");
        await using var api = TenancyApiFactory.GrantAll(fixture.AppConnectionString(world.Database));
        var scopes = api.Services.GetRequiredService<ITenantScopeFactory>();
        var reader = api.Services.GetRequiredService<IIntakeArtifactReader>();

        await using (await scopes.BeginAsync(world.Org, readOnlyTransaction: true, cancellationToken: Token))
        {
            (await reader.ReadEligibleAsync(world.Org, world.Project, artifact, Token)).ShouldNotBeNull().MediaAllowed.ShouldBeFalse();
        }
    }
}
