using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Application;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.1.1-T1.2 without a database: the store refuses a malformed hash and a non-object body before it demands a
/// tenant scope, so a malformed append never reaches a statement.
/// </summary>
public sealed class PolicyRevisionUnitTests
{
    private static string ValidHash() => "sha256:" + new string('a', 64);

    [Fact]
    public async Task Append_RejectsAMalformedHash_WithoutTouchingTheDatabase()
    {
        string[] malformed =
        [
            string.Empty,
            "sha256:",
            "sha256:XYZ",
            "sha256:" + new string('A', 64),
            "SHA256:" + new string('a', 64),
            "sha256:" + new string('a', 63),
            "sha256:" + new string('a', 65),
            new string('a', 64),
        ];

        foreach (var hash in malformed)
        {
            var db = Substitute.For<ITenantDb>();
            var scopes = Substitute.For<ITenantScopeAccessor>();
            var store = new PolicyRevisionStore(db, scopes);

            await Should.ThrowAsync<ArgumentException>(async () =>
                await store.AppendAsync(Guid.CreateVersion7(), "{}", hash, Guid.CreateVersion7(), TestContext.Current.CancellationToken));

            await db.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default, TestContext.Current.CancellationToken);
            scopes.DidNotReceiveWithAnyArgs().Require();
        }
    }

    [Fact]
    public async Task Append_RejectsANonObjectBody_WithoutTouchingTheDatabase()
    {
        string[] malformed = [string.Empty, "not json", "[]", "\"text\"", "42", "null"];

        foreach (var body in malformed)
        {
            var db = Substitute.For<ITenantDb>();
            var scopes = Substitute.For<ITenantScopeAccessor>();
            var store = new PolicyRevisionStore(db, scopes);

            await Should.ThrowAsync<ArgumentException>(async () =>
                await store.AppendAsync(Guid.CreateVersion7(), body, ValidHash(), Guid.CreateVersion7(), TestContext.Current.CancellationToken));

            await db.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default, TestContext.Current.CancellationToken);
            scopes.DidNotReceiveWithAnyArgs().Require();
        }
    }
}
