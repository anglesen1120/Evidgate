using Evidgate.Modules.Authoring.Application.Changes;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The batch ETag of 13.1.2 §5: a hash over every affected item <b>and its current version</b>, in id order, so a set
/// of items has the same tag whichever order the caller read them in and any single moved item changes it.
/// </summary>
public sealed class AuthoringBatchEtagTests
{
    [Fact]
    public void Etag_IsOrderIndependent_AndVersionSensitive()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var third = Guid.CreateVersion7();

        var ascending = new[] { Row(first, 1), Row(second, 4), Row(third, 7) };
        var shuffled = new[] { Row(third, 7), Row(first, 1), Row(second, 4) };

        var etag = AuthoringChangeApplyRules.BatchEtag(ascending);

        // Order independence is what lets two clients that selected the same set in a different order agree on If-Match.
        AuthoringChangeApplyRules.BatchEtag(shuffled).ShouldBe(etag);
        AuthoringChangeApplyRules.BatchEtag(ascending.Reverse()).ShouldBe(etag);

        // One moved version is enough: the very race the header guards against.
        AuthoringChangeApplyRules.BatchEtag([Row(first, 1), Row(second, 5), Row(third, 7)]).ShouldNotBe(etag);
        AuthoringChangeApplyRules.BatchEtag([Row(first, 2), Row(second, 4), Row(third, 7)]).ShouldNotBe(etag);

        // A set that gained or lost an item is a different selection, so it can never reuse the old tag.
        AuthoringChangeApplyRules.BatchEtag([Row(first, 1), Row(second, 4)]).ShouldNotBe(etag);
        AuthoringChangeApplyRules.BatchEtag([.. ascending, Row(Guid.CreateVersion7(), 1)]).ShouldNotBe(etag);
    }

    [Fact]
    public void Etag_IsTheDocumentedOrderHashOfTheCanonicalSet()
    {
        var etag = AuthoringChangeApplyRules.BatchEtag([Row(Guid.CreateVersion7(), 3)]);

        // The helper the reorder precondition already uses: `order:` + the sha256 of the canonical set, whose digest is
        // the 64 lowercase hex digits of 13.1.2 §5 — one scheme for both preconditions.
        etag.ShouldStartWith("order:sha256:");

        var digest = etag["order:sha256:".Length..];
        digest.Length.ShouldBe(64);
        digest.All(character => char.IsAsciiDigit(character) || character is (>= 'a' and <= 'f')).ShouldBeTrue();
    }

    /// <summary>One locked affected item, as the ETag rule reads it.</summary>
    /// <param name="id">The item.</param>
    /// <param name="version">The item's current version.</param>
    private static AuthoringAffectedItemRow Row(Guid id, int version) => new() { Id = id, Version = version };
}
