using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Application;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.1.1-T1.2 without a database: the store refuses a malformed hash and a body that is not a storable JSON object
/// before it demands a tenant scope, so a malformed append never reaches a statement.
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
            // $ would accept these; \z and the Postgres CHECK do not (07.1.1-T1 review C4).
            "sha256:" + new string('a', 64) + "\n",
            "sha256:" + new string('a', 64) + "\r\n",
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
    public async Task Append_RejectsANonObjectOrUnstorableBody_WithoutTouchingTheDatabase()
    {
        string[] malformed =
        [
            string.Empty,
            "not json",
            "[]",
            "\"text\"",
            "42",
            "null",
            // jsonb refuses U+0000 and unpaired surrogates, so the pre-DB check must too (07.1.1-T1 review C5).
            "{\"a\":\"\\u0000\"}",
            "{\"a\":\"\\ud800\"}",
            "{\"\\u0000\":\"x\"}",
        ];

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
