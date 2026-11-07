// The parser registry read against a real PostgreSQL: which release a producer kind resolves to, and the one dotted
// numeric order that decides it — the same order the reprocess route compares a pinned release with.
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The registry read behind a parse: a producer kind resolves to the highest registered release under the dotted
/// numeric order a reprocess also asks "is this newer?" with, the newest registration breaking only a tie between two
/// labels that name the same release, a project-owned row outranking the platform default, and a release the order
/// cannot place never parsing a report.
/// <para>
/// Each case runs on a private database: the catalogue's platform rows belong to no tenant, so they are visible to
/// every org and cannot be isolated by tenant the way the shared database's cases are.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed class ReportParserResolverTests
{
    /// <summary>The registry's <c>parser_id</c> unless a case needs two release ladders side by side.</summary>
    private const string ParserId = "junit-probe";

    private readonly PostgresTenancyFixture _fixture;

    public ReportParserResolverTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>One private database, its host, and the two orgs the scope cases need.</summary>
    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid Org,
        Guid OtherOrg,
        DedicatedDatabase Dedicated) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
            await Dedicated.DisposeAsync();
        }
    }

    [Fact]
    public async Task Resolve_ReleasesRegisteredOutOfOrder_AnswersTheHigherVersion()
    {
        var world = await StartAsync();
        await using var _ = world;
        const string kind = "probe-ladder";

        // The higher release was registered first, so a read that preferred recency or text would answer 1.9.
        await SeedReleaseAsync(world, null, kind, version: "1.10", registeredAt: "2026-01-01T00:00:00Z");
        await SeedReleaseAsync(world, null, kind, version: "1.9", registeredAt: "2026-02-01T00:00:00Z");

        var resolution = await ResolveAsync(world, world.Org, kind);

        ParserVersionOrder.Compare("1.10", "1.9").ShouldBe(1, "the shared order puts 1.10 after 1.9");
        resolution.IsResolved.ShouldBeTrue();
        resolution.ParserId.ShouldBe(ParserId);
        resolution.Version.ShouldBe("1.10");
    }

    [Fact]
    public async Task Resolve_LabelsThatNameTheSameRelease_AnswersTheMostRecentlyRegistered()
    {
        var world = await StartAsync();
        await using var _ = world;
        const string newerTrailingZero = "probe-equal-a";
        const string newerShorter = "probe-equal-b";

        await SeedReleaseAsync(
            world, null, newerTrailingZero, version: "1.10.0", registeredAt: "2026-01-01T00:00:00Z",
            parserId: "junit-probe-a");
        await SeedReleaseAsync(
            world, null, newerTrailingZero, version: "1.10", registeredAt: "2026-02-01T00:00:00Z",
            parserId: "junit-probe-a");

        await SeedReleaseAsync(
            world, null, newerShorter, version: "1.10", registeredAt: "2026-01-01T00:00:00Z", parserId: "junit-probe-b");
        await SeedReleaseAsync(
            world, null, newerShorter, version: "1.10.0", registeredAt: "2026-02-01T00:00:00Z",
            parserId: "junit-probe-b");

        var padded = await ResolveAsync(world, world.Org, newerTrailingZero);
        var shorter = await ResolveAsync(world, world.Org, newerShorter);

        // The shared order calls them the same release, so no text comparison may decide between them.
        ParserVersionOrder.Compare("1.10.0", "1.10").ShouldBe(0);
        padded.Version.ShouldBe("1.10");
        shorter.Version.ShouldBe("1.10.0");
    }

    [Fact]
    public async Task Resolve_AMajorBelowATenDigitMinor_AnswersTheHigherMajor()
    {
        var world = await StartAsync();
        await using var _ = world;
        const string kind = "probe-major";

        await SeedReleaseAsync(world, null, kind, version: "2.0", registeredAt: "2026-01-01T00:00:00Z");
        await SeedReleaseAsync(world, null, kind, version: "1.10.9", registeredAt: "2026-02-01T00:00:00Z");

        (await ResolveAsync(world, world.Org, kind)).Version.ShouldBe("2.0");
    }

    [Fact]
    public async Task Resolve_AReleaseTheOrderCannotPlace_NeverBeatsARankableOne()
    {
        var world = await StartAsync();
        await using var _ = world;
        const string kind = "probe-label";

        await SeedReleaseAsync(world, null, kind, version: "1.9.0", registeredAt: "2026-01-01T00:00:00Z");
        await SeedReleaseAsync(world, null, kind, version: "9.0.0-rc.1", registeredAt: "2026-02-01T00:00:00Z");

        ParserVersionOrder.Compare("9.0.0-rc.1", "1.9.0").ShouldBeNull();
        (await ResolveAsync(world, world.Org, kind)).Version.ShouldBe("1.9.0");
    }

    [Fact]
    public async Task Resolve_AScopeThatHoldsOnlyUnplaceableLabels_AnswersUnsupported()
    {
        var world = await StartAsync();
        await using var _ = world;
        const string kind = "probe-label-only";

        await SeedReleaseAsync(world, null, kind, version: "1.0.0-rc.1", registeredAt: "2026-01-01T00:00:00Z");
        await SeedReleaseAsync(world, null, kind, version: "1.0.0+build", registeredAt: "2026-02-01T00:00:00Z");
        await SeedReleaseAsync(world, null, kind, version: "1..2", registeredAt: "2026-03-01T00:00:00Z");

        var refused = await ResolveAsync(world, world.Org, kind);

        // Only the catalogue changes below, so the refusal is the absence of a release the order can place.
        refused.IsResolved.ShouldBeFalse("no release the order can place exists, so nothing may parse the report");
        refused.Version.ShouldBeNull();
        refused.UnsupportedReason.ShouldBe(ReportParserResolver.UnknownProducerKind);

        await SeedReleaseAsync(world, null, kind, version: "2.0", registeredAt: "2026-04-01T00:00:00Z");

        (await ResolveAsync(world, world.Org, kind)).Version.ShouldBe("2.0");
    }

    [Fact]
    public async Task Resolve_AProjectOwnedRelease_BeatsThePlatformDefault()
    {
        var world = await StartAsync();
        await using var _ = world;
        const string kind = "probe-scope";

        // The project's own release is older and lower, so only the scope rule can make it the one that wins.
        await SeedReleaseAsync(world, null, kind, version: "1.10.0", registeredAt: "2026-01-01T00:00:00Z");
        await SeedReleaseAsync(world, world.Org, kind, version: "1.9.0", registeredAt: "2025-01-01T00:00:00Z");

        (await ResolveAsync(world, world.Org, kind)).Version.ShouldBe("1.9.0");
        (await ResolveAsync(world, world.OtherOrg, kind)).Version.ShouldBe("1.10.0");
    }

    [Fact]
    public async Task Resolve_AnotherOrgsRelease_IsInvisible()
    {
        var world = await StartAsync();
        await using var _ = world;
        const string kind = "probe-foreign";

        await SeedReleaseAsync(world, world.OtherOrg, kind, version: "1.10.0", registeredAt: "2026-01-01T00:00:00Z");

        var owner = await ResolveAsync(world, world.OtherOrg, kind);
        var stranger = await ResolveAsync(world, world.Org, kind);

        owner.Version.ShouldBe("1.10.0");
        stranger.IsResolved.ShouldBeFalse("a release filed under another org is not this org's registry");
        stranger.UnsupportedReason.ShouldBe(ReportParserResolver.UnknownProducerKind);
    }

    [Fact]
    public async Task Resolve_AKindCarryingSqlText_AnswersUnsupportedAndLeavesTheCatalogue()
    {
        var world = await StartAsync();
        await using var _ = world;
        const string kind = "probe-unknown";

        await SeedReleaseAsync(world, null, kind, version: "1.0.0", registeredAt: "2026-01-01T00:00:00Z");
        var before = await CatalogueSizeAsync(world);

        var resolution = await ResolveAsync(world, world.Org, "probe-unknown'; drop table public.report_parser; --");

        resolution.IsResolved.ShouldBeFalse("the kind is the report's own text, and no row names it");
        resolution.UnsupportedReason.ShouldBe(ReportParserResolver.UnknownProducerKind);
        (await CatalogueSizeAsync(world)).ShouldBe(before, "the kind is bound as a parameter, never composed into SQL");
    }

    // ------------------------------------------------------------------ helpers

    private Task<World> StartAsync() =>
        _fixture.StartDedicatedAsync(async dedicated =>
        {
            var database = dedicated.Name;
            var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
            var client = api.CreateClient();
            var user = Guid.CreateVersion7();
            var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
            var otherOrg = await CreateOrgAsync(client, user, Unique.Slug("other"));

            return new World(api, client, database, org, otherOrg, dedicated);
        });

    /// <summary>One catalogue release, written as the superuser: the app role holds SELECT and nothing else.</summary>
    private Task SeedReleaseAsync(
        World world,
        Guid? org,
        string kind,
        string version,
        string registeredAt,
        string parserId = ParserId)
    {
        var owner = org is { } value ? $"'{value:D}'" : "null";

        return _fixture.ExecuteAsync(
            world.Database,
            "insert into public.report_parser "
            + "(id, org_id, parser_id, version, producer_kind, schema_version, notes, created_at) values "
            + $"('{Guid.CreateVersion7():D}', {owner}, '{parserId}', '{version}', '{kind}', 1, 'probe', '{registeredAt}')");
    }

    /// <summary>The call the parse path makes, in a read scope of the org's own.</summary>
    private static async Task<ReportParserResolution> ResolveAsync(World world, Guid org, string kind)
    {
        var token = TestContext.Current.CancellationToken;
        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();

        await using var scope = await scopes.BeginAsync(org, null, readOnlyTransaction: true, token);
        var resolver = provider.GetRequiredService<IReportParserResolver>();

        return await resolver.ResolveAsync(kind, token);
    }

    private Task<long> CatalogueSizeAsync(World world) =>
        _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.report_parser");
}
