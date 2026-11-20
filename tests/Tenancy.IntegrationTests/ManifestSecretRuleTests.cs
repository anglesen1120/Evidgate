using System.Text.Json;
using Evidgate.Modules.Orchestrator.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The manifest's secret check judges a parameter string the way the variant normaliser does: as sent or as stored
/// (enum-like keys fold); an unreadable member name or string is refused by position.
/// Pure: <see cref="ManifestVerifier"/> touches no database.
/// </summary>
public sealed class ManifestSecretRuleTests
{
    private static int[] Positions(string json)
    {
        using var document = JsonDocument.Parse(json);

        return ManifestVerifier.SecretPositions(document.RootElement);
    }

    [Theory]
    [InlineData("""{"mode":"GHP_AAAAAAAA"}""", 1)]
    [InlineData("""{"retries":1,"mode":"GITHUB_PAT_AAAAAAAA"}""", 2)]
    [InlineData("""{"channel":"  XOXB-AAAAAAAA  "}""", 1)]
    [InlineData("""{"mode":" ghp_AAAAAAAA"}""", 1)]
    public void SecretShapedStrings_AreRefusedAtTheirPosition(string json, int position) =>
        Positions(json).ShouldBe([position]);

    [Theory]
    [InlineData("""{"api_token":"secret:ci_token"}""")]
    [InlineData("""{"password":"secret:db.password","browser":"x"}""")]
    [InlineData("""{"retries":3,"headless":true,"x":null}""")]
    [InlineData("""{"mode":"CHROMIUM"}""")]
    [InlineData("""{"mode":"GHP_MODE"}""")]
    public void OrdinaryValues_AreStillAccepted(string json) => Positions(json).ShouldBeEmpty();

    [Fact]
    public void ALoneSurrogateMemberName_IsRefusedByPosition_NotThrown() => Positions("""{"retries":1,"x\udc00y":2}""").ShouldBe([2]);

    [Fact]
    public void ALoneSurrogateInAStringValue_IsRefusedByPosition_NotThrown() =>
        Positions("""{"mode":"a\ud800b"}""").ShouldBe([1]);

    /// <summary>Every refuse-secret vector of the shared variant corpus is refused here; every accept vector is accepted.</summary>
    [Fact]
    public void TheVariantCorpus_AgreesWithTheManifestCheck()
    {
        var path = Path.Combine(RlsExceptionFile.RepositoryRoot, "fixtures", "variant-hash", "vectors.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var checkedRefuse = 0;
        var checkedAccept = 0;
        foreach (var vector in document.RootElement.EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var parameters = vector.GetProperty("params");
            // Arrays and nested objects never reach the manifest check: the manifest schema allows string values only.
            if (parameters.ValueKind != JsonValueKind.Object
                || parameters.EnumerateObject().Any(member => member.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object))
            {
                continue;
            }

            var positions = ManifestVerifier.SecretPositions(parameters);
            if (vector.GetProperty("expect").TryGetProperty("hash", out _) && !name.StartsWith("refuse", StringComparison.Ordinal))
            {
                positions.ShouldBeEmpty(name);
                checkedAccept++;
            }
            else if (name.StartsWith("refuse", StringComparison.Ordinal)
                && vector.GetProperty("expect").GetProperty("problems").EnumerateArray()
                    .Any(problem => problem.GetProperty("reason").GetString() is "secret_value" or "secret_key"
                        || (problem.GetProperty("reason").GetString() == "invalid_key" && name.Contains("surrogate", StringComparison.Ordinal))
                ))
            {
                positions.ShouldNotBeEmpty(name);
                checkedRefuse++;
            }
        }

        // Structural refusals (size, nesting, key shape) are the normaliser's own and not a secret question.
        checkedRefuse.ShouldBeGreaterThanOrEqualTo(8);
        checkedAccept.ShouldBeGreaterThanOrEqualTo(10);
    }
}
