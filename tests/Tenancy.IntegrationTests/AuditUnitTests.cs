using Evidgate.BuildingBlocks.SharedKernel.Security;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>Pure tests of the audit hash rule and the metadata allowlist (02.7.1-T1.2, T2.2).</summary>
public sealed class AuditUnitTests
{
    private static AuditRow Row(long seq = 1, string action = "project.create", IReadOnlyDictionary<string, string>? metadata = null) => new(
        Guid.Parse("0198a000-0000-7000-8000-000000000001"),
        Guid.Parse("0198a000-0000-7000-8000-0000000000aa"),
        seq,
        "user",
        "u-1",
        action,
        "project",
        "p-1",
        null,
        null,
        "req-1",
        "trace-1",
        metadata,
        AuditHashing.Genesis,
        [],
        new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero).AddTicks(10));

    [Fact]
    public void EntryHash_IsDeterministic_And32Bytes()
    {
        var first = AuditHashing.EntryHash(AuditHashing.Genesis, Row());
        var second = AuditHashing.EntryHash(AuditHashing.Genesis, Row());

        first.Length.ShouldBe(32);
        first.ShouldBe(second);
    }

    [Theory]
    [InlineData(2L, "project.create")]
    [InlineData(1L, "project.update")]
    public void EntryHash_CoversTheContent(long seq, string action) =>
        AuditHashing.EntryHash(AuditHashing.Genesis, Row(seq, action)).ShouldNotBe(AuditHashing.EntryHash(AuditHashing.Genesis, Row()));

    [Fact]
    public void EntryHash_CoversThePredecessor()
    {
        var other = new byte[32];
        other[0] = 1;

        AuditHashing.EntryHash(other, Row()).ShouldNotBe(AuditHashing.EntryHash(AuditHashing.Genesis, Row()));
    }

    [Fact]
    public void EntryHash_IgnoresMetadataKeyOrder()
    {
        var a = Row(metadata: new Dictionary<string, string> { ["status"] = "x", ["slug"] = "y" });
        var b = Row(metadata: new Dictionary<string, string> { ["slug"] = "y", ["status"] = "x" });

        AuditHashing.EntryHash(AuditHashing.Genesis, a).ShouldBe(AuditHashing.EntryHash(AuditHashing.Genesis, b));
    }

    [Fact]
    public void Truncate_KeepsMicrosecondPrecision()
    {
        var value = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero).AddTicks(1239);

        AuditHashing.Truncate(value).Ticks.ShouldBe(value.Ticks - 9);
    }

    [Fact]
    public void Metadata_AcceptsAllowlistedPlainValues() =>
        AuditMetadata.Validate(new Dictionary<string, string> { ["slug"] = "acme", ["count"] = "3" }).ShouldNotBeNull();

    [Fact]
    public void Metadata_Null_Or_Empty_IsNull()
    {
        AuditMetadata.Validate(null).ShouldBeNull();
        AuditMetadata.Validate(new Dictionary<string, string>()).ShouldBeNull();
    }

    [Theory]
    [InlineData("password", "hunter2")]
    [InlineData("email", "a@b.test")]
    [InlineData("slug", "Bearer abcdef0123456789")]
    [InlineData("slug", "eyJhbGciOiJIUzI1NiJ9.payload.sig")]
    [InlineData("reason", "token=abc")]
    [InlineData("reason", "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU2Nzg5")]
    public void Metadata_RejectsUnlistedKeysAndSecretShapedValues(string key, string value) =>
        Should.Throw<ArgumentException>(() => AuditMetadata.Validate(new Dictionary<string, string> { [key] = value }));

    [Fact]
    public void Metadata_RejectsTooLongValuesAndTooManyKeys()
    {
        Should.Throw<ArgumentException>(() =>
            AuditMetadata.Validate(new Dictionary<string, string> { ["reason"] = new string('a', AuditMetadata.MaxValueLength + 1) }));

        var many = AuditMetadata.AllowedKeys.ToDictionary(key => key, _ => "v");
        many.Count.ShouldBeLessThanOrEqualTo(AuditMetadata.MaxKeys);
    }
}

/// <summary>02.7.1-T2.1 <c>ActorType_Recorded</c>: the principal-to-actor mapping for every caller kind.</summary>
public sealed class AuditActorTests
{
    [Fact]
    public void ActorType_Recorded_ForEveryCallerKind()
    {
        var user = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();

        Evidgate.BuildingBlocks.Pipeline.Authorization.AuditActorResolver
            .From(Evidgate.BuildingBlocks.Pipeline.Authorization.RequestPrincipal.Member(user))
            .ShouldBe(new AuditActor(AuditActorTypes.User, user.ToString("D")));

        Evidgate.BuildingBlocks.Pipeline.Authorization.AuditActorResolver
            .From(Evidgate.BuildingBlocks.Pipeline.Authorization.RequestPrincipal.Service(project, ["run:create"], "jti-1"))
            .ShouldBe(new AuditActor(AuditActorTypes.CiProducer, "jti-1"));

        Evidgate.BuildingBlocks.Pipeline.Authorization.AuditActorResolver
            .From(Evidgate.BuildingBlocks.Pipeline.Authorization.RequestPrincipal.Service(project, ["run:create"]))
            .ShouldBe(new AuditActor(AuditActorTypes.CiProducer, $"project:{project:D}"));

        Evidgate.BuildingBlocks.Pipeline.Authorization.AuditActorResolver
            .From(Evidgate.BuildingBlocks.Pipeline.Authorization.RequestPrincipal.Operator())
            .Type.ShouldBe(AuditActorTypes.PlatformOperator);

        Evidgate.BuildingBlocks.Pipeline.Authorization.AuditActorResolver.From(null)
            .ShouldBe(new AuditActor(AuditActorTypes.System, "system"));
    }
}
