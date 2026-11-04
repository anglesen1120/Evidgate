// 06.2.1: the shared read projection, exercised directly against a migrated database inside a real tenant scope.
// This is the statement every content path reuses, so its gating and its sensitivity resolution are asserted here
// instead of only through a route.
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application.Artifacts;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Evidence.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.2.1 AC4/AC6: <see cref="ArtifactReadModel"/> gates the three content-bearing members for a caller without
/// <c>artifact:read_sensitive</c>, treats an unscanned row as sensitive, and resolves a kind's default from the
/// project's override before the static map. The scope is the production one (app role, tenant settings, read-only
/// transaction), so row level security applies exactly as it does behind a route.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class EvidenceSensitivityTests
{
    /// <summary>AC4: the run listing hides the key, the content address and the byte count of every row served as
    /// sensitive — stored, policy-raised or unscanned — and shows all of them to a caller who may read sensitive
    /// content.</summary>
    [Fact]
    public async Task ListArtifacts_Sensitive_ObjectKeyHiddenWithoutPermission()
    {
        await using var world = await StartAsync();
        var normal = await SeedArtifactAsync(world, ArtifactKind.RawReport, ArtifactSensitivity.Normal, "clean");
        var sensitive = await SeedArtifactAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean");
        var policyRaised = await SeedArtifactAsync(world, ArtifactKind.Screenshot, ArtifactSensitivity.Normal, "clean");
        await SeedPolicyAsync(world, ArtifactKind.Screenshot, ArtifactSensitivity.Sensitive);

        var gated = await InScopeAsync(world, db => ArtifactReadModel.ReadRunAsync(db, world.RunId, false, Token));
        gated.Count.ShouldBe(3);

        var visible = gated.Single(row => row.Id == normal.Id);
        visible.EffectiveSensitivity.ShouldBe(ArtifactSensitivity.Normal);
        visible.ContentGated.ShouldBeFalse();
        visible.ObjectKey.ShouldBe(normal.ObjectKey);

        foreach (var hidden in new[] { sensitive, policyRaised })
        {
            var row = gated.Single(candidate => candidate.Id == hidden.Id);
            row.EffectiveSensitivity.ShouldBe(
                ArtifactSensitivity.Sensitive, "a project override raises existing rows, it never rewrites them");
            row.ContentGated.ShouldBeTrue();
            row.ObjectKey.ShouldBeNull();
            row.Checksum.ShouldBeNull();
            row.Bytes.ShouldBeNull();
        }

        var ungated = await InScopeAsync(world, db => ArtifactReadModel.ReadRunAsync(db, world.RunId, true, Token));
        ungated.ShouldAllBe(row => !row.ContentGated);
        ungated.Single(row => row.Id == policyRaised.Id).ObjectKey.ShouldBe(policyRaised.ObjectKey);
    }

    /// <summary>AC6: the project's override is the resolver's answer, and a kind with no row falls back to the
    /// static map — so a lowered trace and a raised screenshot both read as committed.</summary>
    [Fact]
    public async Task ResolveDefaultSensitivity_ProjectOverride_TakesPrecedence()
    {
        await using var world = await StartAsync();
        await SeedPolicyAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Normal);
        await SeedPolicyAsync(world, ArtifactKind.Screenshot, ArtifactSensitivity.Sensitive);

        var trace = await InScopeAsync(world, db => ArtifactReadModel.ResolveDefaultSensitivityAsync(
            db, world.Org, world.Project, ArtifactKind.Trace, Token));
        var screenshot = await InScopeAsync(world, db => ArtifactReadModel.ResolveDefaultSensitivityAsync(
            db, world.Org, world.Project, ArtifactKind.Screenshot, Token));
        var noRow = await InScopeAsync(world, db => ArtifactReadModel.ResolveDefaultSensitivityAsync(
            db, world.Org, world.Project, ArtifactKind.DomSnapshot, Token));

        trace.ShouldBe(ArtifactSensitivity.Normal, "the row's override wins over the kind's sensitive default");
        screenshot.ShouldBe(ArtifactSensitivity.Sensitive, "an override can raise a normal kind");
        noRow.ShouldBe(ArtifactSensitivity.Sensitive, "no row means the static map still decides");
    }

    /// <summary>AC4: an unscanned row is served as sensitive, and the scan's <c>pending → clean</c> verdict is what
    /// unmasks it; a caller with the permission sees the content of either.</summary>
    [Fact]
    public async Task ResolveSensitivity_ScanPending_TreatedAsSensitive()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.RawReport, ArtifactSensitivity.Normal, "pending");

        var pending = await InScopeAsync(world, db => ArtifactReadModel.ReadAsync(db, seeded.Id, false, Token));
        pending.ShouldNotBeNull();
        pending.EffectiveSensitivity.ShouldBe(
            ArtifactSensitivity.Sensitive, "an unscanned row has not proved itself clean");
        pending.ContentGated.ShouldBeTrue();
        pending.ObjectKey.ShouldBeNull();
        pending.Checksum.ShouldBeNull();
        pending.Bytes.ShouldBeNull();

        // The permission unmasks content; it never changes what the row is classified as.
        var privileged = await InScopeAsync(world, db => ArtifactReadModel.ReadAsync(db, seeded.Id, true, Token));
        privileged.ShouldNotBeNull();
        privileged.EffectiveSensitivity.ShouldBe(ArtifactSensitivity.Sensitive);
        privileged.ContentGated.ShouldBeFalse("the permission unmasks content, it does not change the classification");
        privileged.ObjectKey.ShouldBe(seeded.ObjectKey);
        privileged.Checksum.ShouldBe(Checksum);

        // The scan's verdict is the only legal move out of pending (0066's guard refuses a completed scan changing).
        await _fixture.ExecuteAsync(
            world.Database, $"update public.artifact set scan_state = 'clean' where id = '{seeded.Id:D}'");

        var clean = await InScopeAsync(world, db => ArtifactReadModel.ReadAsync(db, seeded.Id, false, Token));
        clean.ShouldNotBeNull();
        clean.EffectiveSensitivity.ShouldBe(ArtifactSensitivity.Normal);
        clean.ContentGated.ShouldBeFalse("a clean verdict on a normal kind unmasks the content");
        clean.ObjectKey.ShouldBe(seeded.ObjectKey);
        clean.Checksum.ShouldBe(Checksum);
        clean.Bytes.ShouldBe(Bytes);
    }

    /// <summary>Runs one read inside a production tenant scope: app role, tenant settings, read-only transaction.</summary>
    private static async Task<T> InScopeAsync<T>(World world, Func<ITenantDb, Task<T>> read)
    {
        var host = world.HostFor(RequestPrincipal.Member(world.Founder));
        await using var provider = host.Services.CreateAsyncScope();
        var scopes = provider.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        var db = provider.ServiceProvider.GetRequiredService<ITenantDb>();

        await using var tenant = await scopes.BeginAsync(
            world.Org, world.Project, readOnlyTransaction: true, Token);

        return await read(db);
    }
}
