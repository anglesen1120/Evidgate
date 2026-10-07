using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Application;
using Evidgate.Modules.Policy.Domain;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.1.1-T1.2/T2.1 without a database: the store refuses a body that is not a storable, canonicalisable JSON
/// object — or whose numbers are not exact IEEE 754 doubles (I-JSON, RFC 7493 §2.2) — before it demands a tenant
/// scope, so a malformed append never reaches a statement, and <see cref="PolicyHasher"/> reproduces the RFC 8785
/// vectors the 01/04 suites already assert.
/// </summary>
public sealed class PolicyRevisionUnitTests
{
    private static readonly PolicyHasher Hasher = new();

    [Fact]
    public async Task Append_RejectsANonCanonicalisableBody_WithoutTouchingTheDatabase()
    {
        // A duplicate property name is a JSON object jsonb stores but RFC 8785 cannot canonicalise (00 §7), so the
        // store must refuse it before the scope is demanded — the equivalent of the old caller-hash shape check.
        string[] malformed = ["{\"a\":1,\"a\":2}", "{\"a\":{\"b\":1,\"b\":2}}"];

        foreach (var body in malformed)
        {
            var db = Substitute.For<ITenantDb>();
            var scopes = Substitute.For<ITenantScopeAccessor>();
            var store = new PolicyRevisionStore(db, scopes, Hasher);

            await Should.ThrowAsync<ArgumentException>(async () =>
                await store.AppendAsync(Guid.CreateVersion7(), body, Guid.CreateVersion7(), TestContext.Current.CancellationToken));

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
            var store = new PolicyRevisionStore(db, scopes, Hasher);

            await Should.ThrowAsync<ArgumentException>(async () =>
                await store.AppendAsync(Guid.CreateVersion7(), body, Guid.CreateVersion7(), TestContext.Current.CancellationToken));

            await db.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default, TestContext.Current.CancellationToken);
            scopes.DidNotReceiveWithAnyArgs().Require();
        }
    }

    // ------------------------------------------------------------------ 07.1.1-T2.1: the hasher

    public static TheoryData<string> Rfc8785Vectors =>
        new() { "arrays", "french", "structures", "unicode", "values", "weird" };

    /// <summary>The published RFC 8785 reference pairs (fixtures/rfc8785), the same six the 01.3.2 suite asserts.</summary>
    [Theory]
    [MemberData(nameof(Rfc8785Vectors))]
    public void Hash_ReproducesTheRfc8785PublishedVectors(string name)
    {
        var input = File.ReadAllBytes(Fixtures($"rfc8785/input/{name}.json"));
        var expected = File.ReadAllBytes(Fixtures($"rfc8785/output/{name}.json"));

        Hasher.Hash(Encoding.UTF8.GetString(input)).ShouldBe(CanonicalHash.FromCanonical(expected));
    }

    /// <summary>The golden documents (fixtures/canonical) carry their own canonical text and digest.</summary>
    [Fact]
    public void Hash_ReproducesTheGoldenCanonicalVectors()
    {
        var files = Directory
            .GetFiles(Fixtures("canonical"), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        files.Select(Path.GetFileNameWithoutExtension)
            .ShouldBe(["candidate", "evidence", "manifest", "policy"]);

        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            var vector = document.RootElement;
            var input = vector.GetProperty("input").GetRawText();
            var canonical = vector.GetProperty("canonical").GetString()!;
            var digest = vector.GetProperty("sha256").GetString()!;

            Hasher.Hash(input).ShouldBe(digest, Path.GetFileName(file));
            Hasher.Hash(input).ShouldBe(CanonicalHash.FromCanonical(Encoding.UTF8.GetBytes(canonical)));
        }
    }

    [Fact]
    public void Hash_IgnoresKeyOrderAndWhitespace()
    {
        var compact = Hasher.Hash("""{"b":[2],"a":1}""");
        var formatted = Hasher.Hash("""
            {
              "a": 1,
              "b": [2]
            }
            """);
        // jsonb re-serialises on read: keys reordered and a space after ':' and ','. The canonical form must not care.
        var stored = Hasher.Hash("""{"name":"release gate","rules":[{"op":"gte","threshold":0.995}]}""");
        var reserialised = Hasher.Hash("""{"rules": [{"threshold": 0.995, "op": "gte"}], "name": "release gate"}""");

        compact.ShouldBe(formatted);
        stored.ShouldBe(reserialised);
    }

    [Fact]
    public void Hash_OfADifferentValue_Differs()
    {
        Hasher.Hash("""{"a":1}""").ShouldNotBe(Hasher.Hash("""{"a":2}"""));
    }

    [Fact]
    public void Hash_IsAPrefixedLowercaseSha256()
    {
        Hasher.Hash("""{"a":1}""").ShouldMatch("^sha256:[0-9a-f]{64}$");
    }

    // ------------------------------- 07.1.1-T2.1: numbers must be exact doubles (I-JSON §2.2, PolicyRevisionRules)

    /// <summary>
    /// A number whose exact decimal value is the double it parses to passes the rule, in every spelling of the
    /// value (RFC 8785 §3.2.2.3 canonicalises the double, not the literal).
    /// </summary>
    [Theory]
    [InlineData("{\"a\":1.0}", "{\"a\":1}")]
    [InlineData("{\"a\":1e2}", "{\"a\":100}")]
    [InlineData("{\"a\":0.10}", "{\"a\":0.1}")]
    [InlineData("{\"a\":-0}", "{\"a\":0}")]
    [InlineData("{\"a\":1e-7}", "{\"a\":0.0000001}")]
    public void HasOnlyExactNumbers_AcceptsNumbersExactlyRepresentableAsDoubles(string body, string equivalent)
    {
        PolicyRevisionRules.HasOnlyExactNumbers(body).ShouldBeTrue();
        PolicyRevisionRules.HasOnlyExactNumbers(equivalent).ShouldBeTrue();
    }

    /// <summary>
    /// A number that only exists beyond double precision is refused: RFC 8785 would round it, so two different
    /// bodies would share one content address (finding T1-SEC-S2, I-JSON RFC 7493 §2.2).
    /// </summary>
    [Theory]
    [InlineData("{\"a\":9007199254740993}")]
    [InlineData("{\"a\":0.1000000000000000001}")]
    [InlineData("{\"a\":1.00000000000000001}")]
    public void HasOnlyExactNumbers_RefusesNumbersNotExactlyRepresentableAsDoubles(string body)
    {
        PolicyRevisionRules.HasOnlyExactNumbers(body).ShouldBeFalse();
    }

    [Theory]
    [InlineData("{\"a\":9007199254740993}")]
    [InlineData("{\"a\":0.1000000000000000001}")]
    [InlineData("{\"a\":1.00000000000000001}")]
    public async Task Append_RejectsANumberNotExactlyRepresentableAsADouble_WithoutTouchingTheDatabase(string body)
    {
        var db = Substitute.For<ITenantDb>();
        var scopes = Substitute.For<ITenantScopeAccessor>();
        var store = new PolicyRevisionStore(db, scopes, Hasher);

        await Should.ThrowAsync<ArgumentException>(async () =>
            await store.AppendAsync(Guid.CreateVersion7(), body, Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        await db.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default, TestContext.Current.CancellationToken);
        scopes.DidNotReceiveWithAnyArgs().Require();
    }

    /// <summary>
    /// The refusal never carries the body: the canonicalisation message names the offending property, so the store
    /// must not chain it (finding T1-SEC-S2, review S2).
    /// </summary>
    [Fact]
    public async Task Append_RejectsANonCanonicalisableBody_WithoutLeakingItInTheException()
    {
        var db = Substitute.For<ITenantDb>();
        var scopes = Substitute.For<ITenantScopeAccessor>();
        var store = new PolicyRevisionStore(db, scopes, Hasher);
        const string body = "{\"secretmarker\":1,\"secretmarker\":2}";

        var exception = await Should.ThrowAsync<ArgumentException>(async () =>
            await store.AppendAsync(Guid.CreateVersion7(), body, Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        exception.ToString().ShouldNotContain("secretmarker");
        exception.InnerException.ShouldBeNull();
    }

    /// <summary>A file under the repository's <c>fixtures/</c> directory, resolved as the suite's fixture does.</summary>
    private static string Fixtures(string relativePath) =>
        Path.Combine(RlsExceptionFile.RepositoryRoot, "fixtures", relativePath);
}
