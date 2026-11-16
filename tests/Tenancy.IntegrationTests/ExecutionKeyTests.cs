using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.Modules.Orchestrator.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The <c>execution_key</c> layout of 05.2.1 (00 §7, T10): the expected value is computed here by an independent
/// code path (a byte list, not the shipping span writer), so the test pins the layout, not the implementation.
/// </summary>
public sealed class ExecutionKeyTests
{
    private static readonly Guid CaseId = Guid.Parse("018f2f0e-8a52-7c4b-9d3e-000000000001");

    [Fact]
    public void ExecutionKey_MatchesAnIndependentLayout()
    {
        var variant = "sha256:" + new string('a', 64);

        ExecutionKey.Compute(CaseId, variant, "staging").ShouldBe(Independent(CaseId, variant, "staging"));
        ExecutionKey.Compute(CaseId, variant, "staging").ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ExecutionKey_LengthPrefix_Disambiguates()
    {
        // Without the length prefix, ('ab','c') and ('a','bc') would concatenate to the same bytes.
        ExecutionKey.Compute(Guid.Empty, "ab", "c").ShouldNotBe(ExecutionKey.Compute(Guid.Empty, "a", "bc"));
    }

    [Fact]
    public void ExecutionKey_IsOrderSensitive()
    {
        ExecutionKey.Compute(CaseId, "sha256:" + new string('a', 64), "staging")
            .ShouldNotBe(ExecutionKey.Compute(CaseId, "sha256:" + new string('a', 64), "prod"));
    }

    /// <summary>
    /// <c>fixtures/execution-key/vectors.json</c> is the one corpus both runtimes read; the reporter asserts it in
    /// <c>packages/adapter-playwright/src/executionKey.test.ts</c>.
    /// </summary>
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
    public void ExecutionKey_Vector_Matches_SharedFixture(string name)
    {
        var vector = Vectors[name];

        ExecutionKey.Compute(
                vector.GetProperty("case_id").GetGuid(),
                vector.GetProperty("variant_hash").GetString()!,
                vector.GetProperty("target_env").GetString()!)
            .ShouldBe(vector.GetProperty("expect").GetString());
    }

    private static readonly Dictionary<string, JsonElement> Vectors = LoadVectors();

    private static Dictionary<string, JsonElement> LoadVectors()
    {
        var path = Path.Combine(RlsExceptionFile.RepositoryRoot, "fixtures", "execution-key", "vectors.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.EnumerateArray().ToDictionary(
            vector => vector.GetProperty("name").GetString()!,
            vector => vector.Clone(),
            StringComparer.Ordinal);
    }

    private static string Independent(Guid caseId, string variant, string targetEnv)
    {
        var buffer = new List<byte>();
        foreach (var field in new[] { caseId.ToString("D"), variant, targetEnv })
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            buffer.AddRange(BitConverter.GetBytes(bytes.Length).Reverse()); // big-endian, unlike the shipping writer
            buffer.AddRange(bytes);
        }

        return Convert.ToHexString(SHA256.HashData([.. buffer])).ToLowerInvariant();
    }
}
