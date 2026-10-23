using System.Text.Json;
using Evidgate.Modules.Catalog.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 04.3.2-T1.3 / AC3: the reporter's <c>variant_hash</c> and the server's
/// <see cref="VariantNormalizer"/> must agree on one corpus, so
/// <c>fixtures/variant-hash/vectors.json</c> is asserted here and by
/// <c>packages/adapter-playwright/src/variant.test.ts</c>. The corpus is the arbiter: every
/// expected hash and every expected problem list is what the server produces.
/// </summary>
public sealed class VariantGoldenVectorTests
{
    private const string BasePath = "/params";

    private static readonly Dictionary<string, JsonElement> Vectors = LoadVectors();

    public static TheoryData<string> VectorNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var name in Vectors.Keys)
            {
                names.Add(name);
            }

            return names;
        }
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Vector_Matches_Server_Normaliser(string name)
    {
        var vector = Vectors[name];
        var problems = new List<VariantProblem>();

        var variant = VariantNormalizer.Normalize(vector.GetProperty("params"), BasePath, problems);
        var expect = vector.GetProperty("expect");

        if (expect.TryGetProperty("hash", out var hash))
        {
            problems.ShouldBeEmpty();
            variant.ShouldNotBeNull();
            variant!.Hash.ShouldBe(hash.GetString());
            return;
        }

        variant.ShouldBeNull();
        var expected = expect.GetProperty("problems")
            .EnumerateArray()
            .Select(problem => (Path: problem.GetProperty("path").GetString()!, Reason: problem.GetProperty("reason").GetString()!))
            .ToList();

        problems.Select(problem => (Path: problem.Path, Reason: problem.Reason)).ShouldBe(expected);
    }

    private static Dictionary<string, JsonElement> LoadVectors()
    {
        var path = Path.Combine(RlsExceptionFile.RepositoryRoot, "fixtures", "variant-hash", "vectors.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.EnumerateArray().ToDictionary(
            vector => vector.GetProperty("name").GetString()!,
            vector => vector.Clone(),
            StringComparer.Ordinal);
    }
}
