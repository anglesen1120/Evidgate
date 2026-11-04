// 06.2.1: the shared read projection, exercised directly against a migrated database inside a real tenant scope.
// This is the statement every content path reuses, so its gating is asserted here instead of only through a route.
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Evidence.Application.Artifacts;
using Evidgate.Modules.Evidence.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.2.1 AC4/AC6: <see cref="ArtifactReadModel"/> gates the three content-bearing members for a caller without
/// <c>artifact:read_sensitive</c>, treats an unscanned row as sensitive, and gates a row whose content is being
/// deleted whatever the caller holds. The scope is the production one (app role, tenant settings, read-only
/// transaction), so row level security applies exactly as it does behind a route.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class EvidenceSensitivityTests
{
    /// <summary>AC4: the projection hides the key, the content address and the byte count of every row served as
    /// sensitive — stored, policy-raised or unscanned — and shows all of them to a caller who may read sensitive
    /// content.</summary>
    [Fact]
    public async Task ReadArtifact_Sensitive_ObjectKeyHiddenWithoutPermission()
    {
        await using var world = await StartAsync();
        var normal = await SeedArtifactAsync(world, ArtifactKind.RawReport, ArtifactSensitivity.Normal, "clean");
        var sensitive = await SeedArtifactAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean");
        var policyRaised = await SeedArtifactAsync(world, ArtifactKind.Screenshot, ArtifactSensitivity.Normal, "clean");
        await SeedPolicyAsync(world, ArtifactKind.Screenshot, ArtifactSensitivity.Sensitive);

        var visible = await ReadAsync(world, normal.Id, canReadSensitive: false);
        visible.ShouldNotBeNull();
        visible.EffectiveSensitivity.ShouldBe(ArtifactSensitivity.Normal);
        visible.ContentGated.ShouldBeFalse();
        visible.ObjectKey.ShouldBe(normal.ObjectKey);

        foreach (var hidden in new[] { sensitive, policyRaised })
        {
            var row = await ReadAsync(world, hidden.Id, canReadSensitive: false);
            row.ShouldNotBeNull();
            row.EffectiveSensitivity.ShouldBe(
                ArtifactSensitivity.Sensitive, "a project override raises existing rows, it never rewrites them");
            row.ContentGated.ShouldBeTrue();
            row.ObjectKey.ShouldBeNull();
            row.Checksum.ShouldBeNull();
            row.Bytes.ShouldBeNull();
        }

        var ungated = await ReadAsync(world, policyRaised.Id, canReadSensitive: true);
        ungated.ShouldNotBeNull();
        ungated.ContentGated.ShouldBeFalse();
        ungated.ObjectKey.ShouldBe(policyRaised.ObjectKey);
    }

    /// <summary>AC4: an unscanned row is served as sensitive, and the scan's <c>pending → clean</c> verdict is what
    /// unmasks it; a caller with the permission sees the content of either.</summary>
    [Fact]
    public async Task ResolveSensitivity_ScanPending_TreatedAsSensitive()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.RawReport, ArtifactSensitivity.Normal, "pending");

        var pending = await ReadAsync(world, seeded.Id, canReadSensitive: false);
        pending.ShouldNotBeNull();
        pending.EffectiveSensitivity.ShouldBe(
            ArtifactSensitivity.Sensitive, "an unscanned row has not proved itself clean");
        pending.ContentGated.ShouldBeTrue();
        pending.ObjectKey.ShouldBeNull();
        pending.Checksum.ShouldBeNull();
        pending.Bytes.ShouldBeNull();

        // The permission unmasks content; it never changes what the row is classified as.
        var privileged = await ReadAsync(world, seeded.Id, canReadSensitive: true);
        privileged.ShouldNotBeNull();
        privileged.EffectiveSensitivity.ShouldBe(ArtifactSensitivity.Sensitive);
        privileged.ContentGated.ShouldBeFalse("the permission unmasks content, it does not change the classification");
        privileged.ObjectKey.ShouldBe(seeded.ObjectKey);
        privileged.Checksum.ShouldBe(Checksum);

        // The scan's verdict is the only legal move out of pending (0066's guard refuses a completed scan changing).
        await _fixture.ExecuteAsync(
            world.Database, $"update public.artifact set scan_state = 'clean' where id = '{seeded.Id:D}'");

        var clean = await ReadAsync(world, seeded.Id, canReadSensitive: false);
        clean.ShouldNotBeNull();
        clean.EffectiveSensitivity.ShouldBe(ArtifactSensitivity.Normal);
        clean.ContentGated.ShouldBeFalse("a clean verdict on a normal kind unmasks the content");
        clean.ObjectKey.ShouldBe(seeded.ObjectKey);
        clean.Checksum.ShouldBe(Checksum);
        clean.Bytes.ShouldBe(Bytes);
    }

    /// <summary>
    /// A row whose content is being deleted or is already deleted reads as gated for every caller: the bytes are
    /// gone or going, so no permission unmasks the key, the content address or the length, while the descriptive
    /// columns stay readable.
    /// </summary>
    /// <param name="state">The lifecycle state under test: the soft delete and its terminal form.</param>
    [Theory]
    [InlineData(ArtifactState.PendingDelete)]
    [InlineData(ArtifactState.Deleted)]
    public async Task ReadArtifact_DeletedOrDeletingRow_ContentGatedForEveryCaller(string state)
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(
            world, ArtifactKind.RawReport, ArtifactSensitivity.Normal, "clean", state: state);

        foreach (var canReadSensitive in new[] { false, true })
        {
            var row = await ReadAsync(world, seeded.Id, canReadSensitive);
            row.ShouldNotBeNull();
            row.EffectiveSensitivity.ShouldBe(
                ArtifactSensitivity.Normal, "deletion is not a sensitivity: the classification is unchanged");
            row.ContentGated.ShouldBeTrue("deleted content is gated even for artifact:read_sensitive");
            row.ObjectKey.ShouldBeNull();
            row.Checksum.ShouldBeNull();
            row.Bytes.ShouldBeNull();
            row.State.ShouldBe(state);
        }
    }

    /// <summary>One row through the production projection inside a production tenant scope.</summary>
    private static async Task<ArtifactReadRow?> ReadAsync(World world, Guid artifactId, bool canReadSensitive) =>
        await InScopeAsync(world, db => ArtifactReadModel.ReadAsync(db, artifactId, canReadSensitive, Token));

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
