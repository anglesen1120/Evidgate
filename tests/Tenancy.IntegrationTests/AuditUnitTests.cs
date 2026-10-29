using System.Text.Json;
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
    public void AuditPayload_DenyList_IsOneListAndMatchesNormalisedKeys()
    {
        // 11.9.2-T2.2 (AC10): the one list, mirrored in docs/security/audit-and-deletion-rule.md §4.
        AuditMetadata.ForbiddenKeys.ShouldBe(
        [
            "email",
            "name",
            "ip",
            "user_agent",
            "message",
            "reason_text",
            "comment",
            "note",
            "description",
            "free_text",
            "display_name",
            "email_address",
            "ip_address",
            "full_name",
            "phone",
            "address",
            "username",
        ], ignoreOrder: true);

        AuditMetadata.NormalizeKey("user_agent").ShouldBe("USERAGENT");
        AuditMetadata.NormalizeKey("User-Agent").ShouldBe("USERAGENT");
        AuditMetadata.NormalizeKey("userAgent").ShouldBe("USERAGENT");
        AuditMetadata.NormalizeKey("USER  AGENT").ShouldBe("USERAGENT");
        AuditMetadata.NormalizeKey("email_domain").ShouldBe("EMAILDOMAIN");

        // Unicode compatibility form KC first: a fullwidth spelling normalises to the ASCII key, while a
        // Cyrillic homoglyph is a different letter and only the fail-closed non-ASCII rule refuses it.
        AuditMetadata.NormalizeKey("\uFF45\uFF4D\uFF41\uFF49\uFF4C").ShouldBe("EMAIL");
        AuditMetadata.NormalizeKey("\u0435mail").ShouldNotBe("EMAIL");
    }

    [Fact]
    public void AuditPayload_RefusesForbiddenKeysInFieldsTuplesAndConvertiblePairs()
    {
        // A public instance field is a key name too (a ValueTuple item is reached the same way).
        Should.Throw<ArgumentException>(() => AuditMetadata.EnsureNoForbiddenKeys(new FieldOnlyPayload()))
            .Message.ShouldContain("Email");

        // A ValueTuple's items are walked, so a dictionary nested in a tuple is still refused.
        Should.Throw<ArgumentException>(() => AuditMetadata.EnsureNoForbiddenKeys(
            new ValueTuple<object?, string>(new Dictionary<string, object?> { ["Email"] = "a@b.test" }, "tail")))
            .Message.ShouldContain("Email");

        // A KeyValuePair<TKey, TValue> with a non-string key: the key renders as a name, never skipped.
        Should.Throw<ArgumentException>(() => AuditMetadata.EnsureNoForbiddenKeys(
            new[] { KeyValuePair.Create(PayloadKey.Email, "a@b.test") }))
            .Message.ShouldContain("Email");

        // One such pair held as a plain value (not inside a sequence) is read as a name too.
        Should.Throw<ArgumentException>(() => AuditMetadata.EnsureNoForbiddenKeys(
            new Dictionary<string, object?> { ["holder"] = KeyValuePair.Create(PayloadKey.Email, "a@b.test") }))
            .Message.ShouldContain("Email");

        // The same instance on two paths is not a cycle, so it must still be accepted.
        var shared = new Dictionary<string, object?> { ["slug"] = "acme" };
        AuditMetadata.EnsureNoForbiddenKeys(new object?[] { shared, shared });
    }

    [Fact]
    public void AuditPayload_RefusesNonAsciiKeyThatIsNotTheAsciiName()
    {
        // `еmail` with U+0435 (Cyrillic small ie) normalises to `ЕMAIL`, not `EMAIL`, so the deny list alone
        // would miss it: the walk refuses any key still carrying a non-ASCII letter or digit after form KC.
        var exception = Should.Throw<ArgumentException>(() => AuditMetadata.EnsureNoForbiddenKeys(
            new Dictionary<string, object?> { ["\u0435mail"] = "person@example.test" }));

        exception.Message.ShouldContain("is forbidden");
        exception.Message.ShouldNotContain("person@example.test", Case.Sensitive, "the message never echoes a value");
    }

    [Fact]
    public void AuditPayload_RefusesSelfReferencingCollectionsInsteadOfRecursing()
    {
        var list = new List<object?>();
        list.Add(list);
        Should.Throw<ArgumentException>(() => AuditMetadata.EnsureNoForbiddenKeys(list))
            .Message.ShouldContain("self-referencing");

        var dictionary = new Dictionary<string, object?>();
        dictionary["self"] = dictionary;
        Should.Throw<ArgumentException>(() => AuditMetadata.EnsureNoForbiddenKeys(dictionary))
            .Message.ShouldContain("self-referencing");
    }

    [Fact]
    public void AuditPayload_RefusesPayloadDeeperThanTheCap()
    {
        AuditMetadata.EnsureNoForbiddenKeys(Nest(AuditMetadata.MaxPayloadDepth));

        Should.Throw<ArgumentException>(() =>
                AuditMetadata.EnsureNoForbiddenKeys(Nest(AuditMetadata.MaxPayloadDepth + 1)))
            .Message.ShouldContain($"deeper than {AuditMetadata.MaxPayloadDepth} levels");

        static Dictionary<string, object?> Nest(int levels)
        {
            object? inner = "leaf";
            for (var index = 0; index < levels; index++)
            {
                inner = new Dictionary<string, object?> { [$"level{index}"] = inner };
            }

            return (Dictionary<string, object?>)inner!;
        }
    }

    [Fact]
    public void AuditPayload_WalkReachesJsonElementAndNamesTheKeyNotItsAncestors()
    {
        using var document = JsonDocument.Parse("""{"wrapper":{"email":"person@example.test"}}""");

        var exception = Should.Throw<ArgumentException>(() =>
            AuditMetadata.EnsureNoForbiddenKeys(document.RootElement));

        exception.Message.ShouldContain("email");
        exception.Message.ShouldContain("at depth 2");
        exception.Message.ShouldNotContain("wrapper", Case.Sensitive, "an ancestor key name may itself be personal data");
        exception.Message.ShouldNotContain("person@example.test", Case.Sensitive, "never echo the value of a personal field");
    }

    private enum PayloadKey
    {
        Email,
    }

    private sealed class FieldOnlyPayload
    {
        public string Email = "person@example.test";
    }

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

        // The allowlist may carry MORE keys than one row may use (F55 added `target_user_id` to the
        // seventeen-entry list), so the cap is proven on an entry that takes one allowlisted key too
        // many: every key is valid, the count alone is what refuses it.
        AuditMetadata.AllowedKeys.Count.ShouldBeGreaterThan(AuditMetadata.MaxKeys);

        var tooMany = AuditMetadata.AllowedKeys
            .Take(AuditMetadata.MaxKeys + 1)
            .ToDictionary(key => key, _ => "v");

        Should.Throw<ArgumentException>(() => AuditMetadata.Validate(tooMany));
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
