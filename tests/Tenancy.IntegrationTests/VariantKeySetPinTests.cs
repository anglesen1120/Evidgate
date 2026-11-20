using System.Reflection;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Evidgate.Modules.Catalog.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The enum-like and non-identifying key sets decide which values get the case fold and the secret scan, and the
/// TypeScript normaliser holds its own copy. <c>fixtures/variant-hash/key-sets.json</c> pins both; the TypeScript side
/// is asserted by <c>packages/variant/src/keySets.test.ts</c>. The sets are private, so they are read by reflection.
/// </summary>
public sealed class VariantKeySetPinTests
{
    [Theory]
    [InlineData("EnumLike", "enum_like")]
    [InlineData("NonIdentifying", "non_identifying")]
    public void ServerKeySet_EqualsTheSharedPin(string field, string pinName)
    {
        var actual = ReadServerSet(field);
        var pinned = ReadPin(pinName);
        pinned.Count.ShouldBeGreaterThan(0, $"{pinName} is missing or empty in key-sets.json");

        var missing = pinned.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var extra = actual.Except(pinned, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        (missing.Count + extra.Count).ShouldBe(
            0,
            $"{pinName} drifted from fixtures/variant-hash/key-sets.json: missing on the server [{string.Join(", ", missing)}], extra on the server [{string.Join(", ", extra)}]");
    }

    /// <summary>Every pinned enum-like key folds an upper-case secret-shaped token, so it is refused rather than stored lower-cased.</summary>
    [Theory]
    [MemberData(nameof(EnumLikePin))]
    public void EnumLikeKey_FoldsASecretShapedToken_ToARefusal(string key)
    {
        var problems = new List<VariantProblem>();
        var variant = VariantNormalizer.Normalize(JsonSerializer.SerializeToElement(new Dictionary<string, string> { [key] = "  GHP_AAAAAAAA  " }), "/params", problems);

        variant.ShouldBeNull();
        problems.ShouldHaveSingleItem().ShouldBe(new VariantProblem($"/params/{key}", "secret_value"));
    }

    /// <summary>Every pinned non-identifying key is left out of the hash.</summary>
    [Theory]
    [MemberData(nameof(NonIdentifyingPin))]
    public void NonIdentifyingKey_LeavesTheHashUnchanged(string key)
    {
        var problems = new List<VariantProblem>();
        var bare = VariantNormalizer.Normalize(JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["mode"] = "ci" }), "/params", problems);
        var withKey = VariantNormalizer.Normalize(JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["mode"] = "ci", [key] = "free text" }), "/params", problems);

        problems.ShouldBeEmpty();
        bare.ShouldNotBeNull();
        withKey.ShouldNotBeNull().Hash.ShouldBe(bare.Hash);
    }

    public static TheoryData<string> EnumLikePin() => PinData("enum_like");

    public static TheoryData<string> NonIdentifyingPin() => PinData("non_identifying");

    private static TheoryData<string> PinData(string name)
    {
        var data = new TheoryData<string>();
        foreach (var key in ReadPin(name))
        {
            data.Add(key);
        }

        return data;
    }

    private static HashSet<string> ReadServerSet(string field)
    {
        var info = typeof(ParameterSecretRule).GetField(field, BindingFlags.NonPublic | BindingFlags.Static);
        info.ShouldNotBeNull($"ParameterSecretRule.{field} was renamed or removed: update this pin test");
        var set = info.GetValue(null) as HashSet<string>;
        set.ShouldNotBeNull($"ParameterSecretRule.{field} is no longer a HashSet<string>: update this pin test");
        set.Count.ShouldBeGreaterThan(0, $"ParameterSecretRule.{field} is empty");

        return set.ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ReadPin(string name)
    {
        var path = Path.Combine(RlsExceptionFile.RepositoryRoot, "fixtures", "variant-hash", "key-sets.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.GetProperty(name).EnumerateArray().Select(key => key.GetString()!).ToHashSet(StringComparer.Ordinal);
    }
}
