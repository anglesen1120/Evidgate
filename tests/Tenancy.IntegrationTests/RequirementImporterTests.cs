using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Application.Import;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Requirement rows through <see cref="AuthoringImporter"/> in a real tenant scope: the outline lands as requirements with
/// immutable versions and source <c>import</c>, a refused file writes nothing, and a re-import writes only what changed.
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class RequirementImporterTests
{
    private readonly PostgresTenancyFixture _fixture;

    /// <summary>Wires the fixture.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public RequirementImporterTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    [Fact]
    public async Task Import_Outline_CreatesRequirementsWithParentsKindsAndImportSource()
    {
        await using var world = await StartAsync();
        var rows = RequirementSample.Outline();
        rows.Add(RequirementSample.Row(4, "e1", null, "epic", "Payments"));
        rows.Add(RequirementSample.Row(5, "p1", "e1", "spec_section", "Gateway", "Spec text"));

        var result = await ImportAsync(world, world.Project, rows);

        var report = result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error?.Message);
        report.Counts.ShouldBe(new ImportCounts(5, 0, 0, 0));
        report.BatchId.ShouldNotBeNull();
        report.Rows.Select(row => (row.Index, row.Key, row.Kind, row.Outcome, row.Code)).ShouldBe(
            rows.Select(row => (row.Index, row.Key, ImportRowKind.Requirement, "create", (string?)null)));

        (await ListAsync(world)).ShouldBe(
        [
            "acceptance_criterion|Card is charged|Checkout",
            "acceptance_criterion|Receipt is sent|Checkout",
            "epic|Payments|",
            "spec_section|Gateway|Payments",
            "user_story|Checkout|",
        ]);
        (await Scalar<int>(world, "select count(*)::int from public.requirement_version")).ShouldBe(5);
        (await _fixture.ColumnAsync<string>(
            world.Database,
            $"select distinct source from public.requirement_version where org_id = '{world.Org:D}'"))
            .ShouldBe(["import"]);
    }

    [Fact]
    public async Task Import_DryRun_ReportsCreatesAndWritesNothing()
    {
        await using var world = await StartAsync();

        var result = await ImportAsync(world, world.Project, RequirementSample.Outline(), dryRun: true);

        result.IsSuccess.ShouldBeTrue();
        result.Value.BatchId.ShouldBeNull();
        result.Value.Counts.Created.ShouldBe(3);
        (await Scalar<int>(world, "select count(*)::int from public.requirement")).ShouldBe(0);
        (await Scalar<int>(world, "select count(*)::int from public.requirement_version")).ShouldBe(0);
    }

    [Fact]
    public async Task Import_SameFileTwice_SecondRunIsAllUnchangedWithNoVersionAndNoBatch()
    {
        await using var world = await StartAsync();
        (await ImportAsync(world, world.Project, RequirementSample.Outline())).IsSuccess.ShouldBeTrue();

        var again = await ImportAsync(world, world.Project, RequirementSample.Outline());

        again.IsSuccess.ShouldBeTrue();
        again.Value.Counts.ShouldBe(new ImportCounts(0, 0, 0, 3));
        again.Value.BatchId.ShouldBeNull();
        (await Scalar<int>(world, "select count(*)::int from public.requirement")).ShouldBe(3);
        (await Scalar<int>(world, "select count(*)::int from public.requirement_version")).ShouldBe(3);
    }

    [Fact]
    public async Task Import_ChangedCriterionBody_AppendsExactlyOneVersionToThatRequirement()
    {
        await using var world = await StartAsync();
        (await ImportAsync(world, world.Project, RequirementSample.Outline())).IsSuccess.ShouldBeTrue();
        var changed = RequirementSample.Outline();
        changed[2] = RequirementSample.Row(3, "a2", "s1", "acceptance_criterion", "Receipt is sent", "Receipt is sent by e-mail");

        var result = await ImportAsync(world, world.Project, changed);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Counts.ShouldBe(new ImportCounts(0, 1, 0, 2));
        result.Value.Rows.Select(row => row.Outcome).ShouldBe(["unchanged", "unchanged", "update"]);
        (await Scalar<int>(world, "select count(*)::int from public.requirement_version")).ShouldBe(4);
        (await Scalar<int>(
            world,
            "select head_version_no from public.requirement",
            "and title = 'Receipt is sent'")).ShouldBe(2);
    }

    [Fact]
    public async Task Import_BodyOfTwentyThousandCodePoints_IsStoredAndOneMoreIsRefusedWithNothingWritten()
    {
        await using var world = await StartAsync();
        var accepted = await ImportAsync(
            world, world.Project, [RequirementSample.Row(1, "s1", null, "user_story", "Big", new string('x', 20_000))]);
        accepted.IsSuccess.ShouldBeTrue();
        (await Scalar<int>(world, "select octet_length(body) from public.requirement_version")).ShouldBe(20_000);

        var refused = await ImportAsync(
            world,
            world.Project,
            [
                RequirementSample.Row(1, "s1", null, "user_story", "Other", "fine"),
                RequirementSample.Row(2, "s2", null, "user_story", "Huge", new string('x', 20_001)),
            ]);

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Details.ShouldBe([new Evidgate.BuildingBlocks.SharedKernel.Api.ErrorDetail("/description", "length", 2)]);
        (await Scalar<int>(world, "select count(*)::int from public.requirement")).ShouldBe(1);
    }

    [Fact]
    public async Task Import_FeatureNodeOfAnotherProject_IsRefusedAndNothingIsWritten()
    {
        await using var world = await StartAsync();
        var other = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("billing"));
        var foreign = await NodeIdAsync(world, other);
        var own = await NodeIdAsync(world, world.Project);

        var refused = await ImportAsync(
            world,
            world.Project,
            [
                RequirementSample.Row(1, "s1", null, "user_story", "Linked", featureNode: own.ToString("D")),
                RequirementSample.Row(2, "s2", null, "user_story", "Foreign", featureNode: foreign.ToString("D")),
            ]);

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Details.ShouldBe([new Evidgate.BuildingBlocks.SharedKernel.Api.ErrorDetail("/item_key", "unknown_feature_node", 2)]);
        (await Scalar<int>(world, "select count(*)::int from public.requirement")).ShouldBe(0);

        var linked = await ImportAsync(
            world, world.Project, [RequirementSample.Row(1, "s1", null, "user_story", "Linked", featureNode: own.ToString("D"))]);
        linked.IsSuccess.ShouldBeTrue();
        (await Scalar<Guid>(world, "select feature_node_id from public.requirement")).ShouldBe(own);
    }

    [Fact]
    public async Task Import_TwoStoredRequirementsOfOneTitle_IsAmbiguousAndNothingIsWritten()
    {
        await using var world = await StartAsync();
        (await ImportAsync(
            world,
            world.Project,
            [
                RequirementSample.Row(1, "s1", null, "user_story", "Twin", "one"),
                RequirementSample.Row(2, "s2", null, "user_story", "Twin", "two"),
            ])).IsSuccess.ShouldBeTrue();

        var again = await ImportAsync(
            world, world.Project, [RequirementSample.Row(1, "s1", null, "user_story", "Twin", "changed")]);

        again.IsSuccess.ShouldBeFalse();
        again.Error!.Details.ShouldBe([new Evidgate.BuildingBlocks.SharedKernel.Api.ErrorDetail("/title", "ambiguous_match", 1)]);
        (await Scalar<int>(world, "select count(*)::int from public.requirement_version")).ShouldBe(2);
    }

    [Fact]
    public async Task Import_MarkdownAmbiguousTitle_ReportsHeadingSourceLine()
    {
        await using var world = await StartAsync();
        (await ImportAsync(
            world,
            world.Project,
            [
                RequirementSample.Row(1, "s1", null, "user_story", "Twin", "one"),
                RequirementSample.Row(2, "s2", null, "user_story", "Twin", "two"),
            ])).IsSuccess.ShouldBeTrue();
        var parsed = MarkdownFormat.Read(Encoding.UTF8.GetBytes("\n\n# Twin\n- Criterion"));
        parsed.IsSuccess.ShouldBeTrue();

        var result = await ImportAsync(world, world.Project, parsed.Rows);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Details.ShouldBe([new Evidgate.BuildingBlocks.SharedKernel.Api.ErrorDetail("/title", "ambiguous_match", 3)]);
        (await Scalar<int>(world, "select count(*)::int from public.requirement_version")).ShouldBe(2);

        var unique = MarkdownFormat.Read(Encoding.UTF8.GetBytes("\n\n# Unique\n- Criterion"));
        var accepted = await ImportAsync(world, world.Project, unique.Rows);
        accepted.IsSuccess.ShouldBeTrue();
        accepted.Value.Rows.Select(row => row.Index).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Import_SameTitleUnderDifferentParentsOrKinds_ResolvesEachIdentity()
    {
        await using var world = await StartAsync();
        ImportRow[] rows =
        [
            RequirementSample.Row(1, "s1", null, "user_story", "First"),
            RequirementSample.Row(2, "a1", "s1", "acceptance_criterion", "Shared"),
            RequirementSample.Row(3, "s2", null, "user_story", "Second"),
            RequirementSample.Row(4, "a2", "s2", "acceptance_criterion", "Shared"),
            RequirementSample.Row(5, "s3", null, "user_story", "Shared"),
            RequirementSample.Row(6, "e1", null, "epic", "Shared"),
        ];
        (await ImportAsync(world, world.Project, rows)).IsSuccess.ShouldBeTrue();

        var again = await ImportAsync(world, world.Project, rows);

        again.IsSuccess.ShouldBeTrue();
        again.Value.Counts.ShouldBe(new ImportCounts(0, 0, 0, 6));
        (await Scalar<int>(world, "select count(*)::int from public.requirement_version")).ShouldBe(6);
    }

    [Fact]
    public async Task Import_ReferenceCode_UpdatesTheSameRequirementAndABlankCodeKeepsIt()
    {
        await using var world = await StartAsync();
        (await ImportAsync(
            world, world.Project, [RequirementSample.Row(1, "s1", null, "user_story", "First title", "b", "REQ-1")])).IsSuccess.ShouldBeTrue();
        var id = await Scalar<Guid>(world, "select id from public.requirement");

        var renamed = await ImportAsync(
            world, world.Project, [RequirementSample.Row(1, "s1", null, "user_story", "Second title", "b", "REQ-1")]);

        renamed.IsSuccess.ShouldBeTrue();
        renamed.Value.Counts.ShouldBe(new ImportCounts(0, 1, 0, 0));
        (await Scalar<Guid>(world, "select id from public.requirement")).ShouldBe(id);
        (await Scalar<string>(world, "select title from public.requirement")).ShouldBe("Second title");

        var blank = await ImportAsync(
            world, world.Project, [RequirementSample.Row(1, "s1", null, "user_story", "Second title", "b")]);

        blank.IsSuccess.ShouldBeTrue();
        blank.Value.Counts.ShouldBe(new ImportCounts(0, 0, 0, 1));
        (await Scalar<string>(world, "select external_ref from public.requirement")).ShouldBe("REQ-1");
    }

    [Fact]
    public async Task Import_OneInvalidRowAmongValidOnes_LeavesNoRequirement()
    {
        await using var world = await StartAsync();
        var rows = RequirementSample.Outline();
        rows.Add(RequirementSample.Row(4, "a3", "s1", "not_a_kind", "Bad"));

        var refused = await ImportAsync(world, world.Project, rows);

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Details.ShouldBe([new Evidgate.BuildingBlocks.SharedKernel.Api.ErrorDetail("/node_kind", "value_invalid", 4)]);
        (await Scalar<int>(world, "select count(*)::int from public.requirement")).ShouldBe(0);
    }

    [Fact]
    public async Task Import_TreeRowsMixedWithRequirementRows_IsRefusedWhole()
    {
        await using var world = await StartAsync();
        var rows = RequirementSample.Outline();
        rows.Add(ImportSample.Node(4, "n1", null, "module", "Module"));

        var refused = await ImportAsync(world, world.Project, rows);

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Details.ShouldBe([new Evidgate.BuildingBlocks.SharedKernel.Api.ErrorDetail("/kind", "value_invalid", 4)]);
        (await Scalar<int>(world, "select count(*)::int from public.requirement")).ShouldBe(0);
        (await Scalar<int>(world, "select count(*)::int from public.feature_node")).ShouldBe(0);
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        return new World(api, client, database, user, org, project);
    }

    private static async Task<Result<ImportReport>> ImportAsync(
        World world,
        Guid projectId,
        IReadOnlyList<ImportRow> rows,
        bool dryRun = false)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, projectId, false, cancellationToken);

        var importer = new AuthoringImporter(
            services.ServiceProvider.GetRequiredService<ITenantDb>(),
            services.ServiceProvider.GetRequiredService<AuthoringCodeAllocator>(),
            services.ServiceProvider.GetRequiredService<ManualCaseStepsValidator>(),
            services.ServiceProvider.GetRequiredService<RequirementWriter>());

        var result = await importer.ImportAsync(world.Org, projectId, null, rows, dryRun, cancellationToken);
        await scope.CommitAsync(cancellationToken);

        return result;
    }

    /// <summary>Creates one module in a project through the tree importer and returns its id.</summary>
    private async Task<Guid> NodeIdAsync(World world, Guid projectId)
    {
        var result = await ImportAsync(world, projectId, [ImportSample.Node(1, "n1", null, "module", "Module")]);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        return await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.feature_node where org_id = '{world.Org:D}' and project_id = '{projectId:D}'");
    }

    /// <summary><c>kind|title|parent title</c> of every requirement of the world's project, ordered by title.</summary>
    private Task<IReadOnlyList<string>> ListAsync(World world) => _fixture.ColumnAsync<string>(
        world.Database,
        $"""
        select r.kind || '|' || r.title || '|' || coalesce(p.title, '')
          from public.requirement r
          left join public.requirement p on p.org_id = r.org_id and p.id = r.parent_requirement_id
         where r.org_id = '{world.Org:D}' and r.project_id = '{world.Project:D}'
         order by r.kind collate "C", r.title collate "C"
        """);

    /// <summary>One scalar of the world's project: <paramref name="select"/> names the table, the tenant filter is appended.</summary>
    private Task<T> Scalar<T>(World world, string select, string? and = null) => _fixture.ScalarAsync<T>(
        world.Database,
        $"{select} where org_id = '{world.Org:D}' and project_id = '{world.Project:D}' {and}");
}
