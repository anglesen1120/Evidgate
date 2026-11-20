using System.Text.Json;
using Evidgate.Modules.Orchestrator.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The manifest's secret check judges a parameter the way the variant normaliser does: a string as sent or as stored
/// (enum-like keys fold), array items and nested members each as a value, an unreadable member name by position.
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
    [InlineData("""{"tags":["ghp_AAAAAAAAAAAAAAAAAAAA"]}""", 1)]
    [InlineData("""{"locale":["en","GHP_AAAAAAAA"]}""", 1)]
    [InlineData("""{"x":1,"n":{"k":"ghp_AAAAAAAAAAAAAAAAAAAA"}}""", 2)]
    [InlineData("""{"n":{"deep":[["ghp_AAAAAAAAAAAAAAAAAAAA"]]}}""", 1)]
    [InlineData("""{"n":{"api_token":"x"}}""", 1)]
    [InlineData("""{"tags":["secret:ghp_AAAAAAAAAAAAAAAAAAAA"]}""", 1)]
    public void SecretShapedValues_AreRefusedAtTheirTopLevelPosition(string json, int position) =>
        Positions(json).ShouldBe([position]);

    [Theory]
    [InlineData("""{"tags":["x","secret:ghp_AAAA"]}""")]
    [InlineData("""{"mode":"CHROMIUM"}""")]
    [InlineData("""{"mode":"GHP_MODE"}""")]
    [InlineData("""{"n":{"k":"plain"},"retries":3,"headless":true,"x":null}""")]
    [InlineData("""{"list":["a","b","c","d","e","f","g","h","i","j","k","l","m","n","o","p","q","r","s","t","u","v","w","x","y","z"]}""")]
    public void OrdinaryValues_AreStillAccepted(string json) => Positions(json).ShouldBeEmpty();

    /// <summary>An array item must get the answer the same string gets as a scalar, whatever that answer is.</summary>
    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    [InlineData("3f2b8c1e-5d4a-4f6b-9a7c-0d1e2f3a4b5c")]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("1.2.3")]
    [InlineData("v10.0.1-rc.1")]
    [InlineData("https://staging.example.test/path?q=1")]
    [InlineData("A long human readable sentence describing what this variant exercises in the checkout flow.")]
    [InlineData("en-US")]
    public void AnArrayItem_GetsTheAnswerTheSameStringGetsAsAScalar(string text)
    {
        var scalar = Positions(JsonSerializer.Serialize(new { k = text }));
        var item = Positions(JsonSerializer.Serialize(new { k = new[] { "x", text } }));
        var nested = Positions(JsonSerializer.Serialize(new { k = new { inner = text } }));

        item.ShouldBe(scalar);
        nested.ShouldBe(scalar);
    }

    [Fact]
    public void ALoneSurrogateMemberName_IsRefusedByPosition_NotThrown()
    {
        Positions("""{"retries":1,"x\udc00y":2}""").ShouldBe([2]);
        Positions("""{"n":{"\ud800":1}}""").ShouldBe([1]);
    }

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
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var positions = ManifestVerifier.SecretPositions(parameters);
            if (name.StartsWith("accept", StringComparison.Ordinal))
            {
                positions.ShouldBeEmpty(name);
                checkedAccept++;
            }
            else if (name.StartsWith("refuse", StringComparison.Ordinal)
                && vector.GetProperty("expect").GetProperty("problems").EnumerateArray()
                    .Any(problem => problem.GetProperty("reason").GetString() is "secret_value" or "secret_key"
                        || (problem.GetProperty("reason").GetString() == "invalid_key" && name.Contains("surrogate", StringComparison.Ordinal))))
            {
                positions.ShouldNotBeEmpty(name);
                checkedRefuse++;
            }
        }

        // Structural refusals (size, nesting, key shape) are the normaliser's own and not a secret question.
        checkedRefuse.ShouldBeGreaterThanOrEqualTo(10);
        checkedAccept.ShouldBeGreaterThanOrEqualTo(5);
    }
}
