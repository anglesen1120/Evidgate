using Amazon.S3;
using Amazon.S3.Model;
using Shouldly;
using Xunit;

namespace ObjectStorage.IntegrationTests;

/// <summary>
/// AC7 (06.1.2-T1): the bucket carries the same <c>pending/</c> 24 h lifecycle rule
/// the dev stack puts on its bucket, so an abandoned staging object is deleted after
/// a day. The fixture applies the rule parsed from the committed XML the dev stack
/// uploads, so the two cannot drift apart.
/// </summary>
[Collection(SeaweedFsCollectionDefinition.Name)]
[Trait("Category", "Docker")]
public sealed class PendingLifecycleTests(SeaweedFsFixture fixture)
{
    [Fact]
    public async Task PendingLifecycle_Rule_IsPresent_24h()
    {
        using var client = fixture.Target.CreateClient();
        var response = await client.GetLifecycleConfigurationAsync(
            new GetLifecycleConfigurationRequest { BucketName = fixture.Target.Bucket },
            TestContext.Current.CancellationToken);

        var rule = response.Configuration.Rules.ShouldHaveSingleItem();
        rule.Id.ShouldBe(fixture.PendingLifecycle.Id);
        rule.Status.ShouldBe(LifecycleRuleStatus.Enabled);
        rule.Expiration.Days.ShouldBe(fixture.PendingLifecycle.ExpirationDays);
        rule.Filter.LifecycleFilterPredicate.ShouldBeOfType<LifecyclePrefixPredicate>()
            .Prefix.ShouldBe(fixture.PendingLifecycle.Prefix);

        // AC7: the committed rule is the documented pending/ 24 h rule.
        fixture.PendingLifecycle.Id.ShouldBe("evidgate-pending-24h");
        fixture.PendingLifecycle.Prefix.ShouldBe("pending/");
        fixture.PendingLifecycle.ExpirationDays.ShouldBe(1);
    }
}
