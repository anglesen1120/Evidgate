// The same billing key in two tenants: the unique key is (org_id, billing_key), so each org may hold its own row for one
// key with a different payload. Neither insert conflicts and the conflict counter never moves.
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The tenant half of the payload comparison: a billing key is unique per org, so another org's row under the same key is
/// not a payload conflict and is never counted as one.
/// </summary>
public sealed partial class UsageLedgerPayloadHashTests
{
    [Fact]
    public async Task SameBillingKey_DifferentPayloadsInTwoOrgs_AreIndependent()
    {
        await using var world = await StartAsync();
        var key = Unique.Slug("two-orgs");
        var otherOrg = await CreateOrgAsync(world.Client, world.User, Unique.Slug("globex"));
        var otherProject = await CreateProjectAsync(world.Client, world.User, otherOrg, Unique.Slug("billing"));

        using var listener = ConflictCounter(out var measured);

        var own = await RecordAsync(world, world.Org, [Entry(key, world.Project, quantity: 1m)]);
        var foreign = await RecordAsync(
            world,
            otherOrg,
            [Entry(key, otherProject, quantity: 9m, unit: "furlong")]);

        own.Conflicts.ShouldBeEmpty();
        own.Inserted.ShouldBe([key], "the first org's key inserts");
        foreign.Conflicts.ShouldBeEmpty();
        foreign.Inserted.ShouldBe([key], "another org's row for the same key is a different row, not a conflict");
        measured.ShouldBeEmpty("no key conflicted, so the counter never moves");
        (await LedgerRowsAsync(world, key)).ShouldBe(2, "one row per org");
    }

    /// <summary>Every ledger row of <paramref name="billingKey"/> across tenants: the superuser read sees both orgs.</summary>
    /// <param name="world">The world.</param>
    /// <param name="billingKey">The key both orgs wrote.</param>
    private Task<long> LedgerRowsAsync(World world, string billingKey) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.usage_ledger_entry where billing_key = '{billingKey}'");
}
