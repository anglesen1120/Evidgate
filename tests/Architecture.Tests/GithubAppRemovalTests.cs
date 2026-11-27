using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// The platform is not a GitHub App: no host, secret list, configuration matrix or inventory row asks for an App
/// id, private key, client secret, slug or API/OAuth base address, and the GitHub client building block is gone.
/// GitHub sign-in stays and keeps its own <c>EVIDGATE__OAUTH__GITHUB__*</c> keys; the scans keep that positive
/// control so an emptied list cannot pass.
/// </summary>
public class GithubAppRemovalTests
{
    /// <summary>The prefix every App setting carried; sign-in settings live under <c>OAUTH__GITHUB</c> instead.</summary>
    private const string AppPrefix = "EVIDGATE__GITHUB__";

    [Fact]
    public void SecretNames_HoldNoGithubAppSecret_AndKeepTheLoginSecret()
    {
        SecretNames.All.ShouldContain(SecretNames.OauthGithubClientSecret);
        SecretNames.All.Where(IsAppKey).ShouldBeEmpty("an App secret must not be resolved by any host");
    }

    [Fact]
    public void ConfigMatrix_HoldsNoGithubAppKey_AndKeepsTheLoginKeys()
    {
        var keys = NamesIn("infra/config-matrix.json", "key");

        keys.ShouldContain("EVIDGATE__OAUTH__GITHUB__CLIENTID");
        keys.ShouldContain(SecretNames.OauthGithubClientSecret);
        keys.Where(IsAppKey).ShouldBeEmpty("a host would be configured for an App that no longer exists");
    }

    [Fact]
    public void SecretsInventory_HoldsNoGithubAppSecret_AndKeepsTheLoginSecret()
    {
        var names = NamesIn("infra/secrets-inventory.json", "name");

        names.ShouldContain(SecretNames.OauthGithubClientSecret);
        names.Where(IsAppKey).ShouldBeEmpty("an App secret would still be rotated and audited");
    }

    [Fact]
    public void GithubClientBuildingBlock_IsGone()
    {
        var directory = Path.Combine(SolutionLayout.RepoRoot, "src", "BuildingBlocks", "GitHub");
        var sourceFiles = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.cs*", SearchOption.AllDirectories).ToArray()
            : Array.Empty<string>();

        // A leftover bin/obj folder is not the block; a project or source file is.
        sourceFiles
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ShouldBeEmpty("nothing in the platform calls GitHub's REST API any more");
    }

    [Theory]
    [InlineData("EVIDGATE__GITHUB__PRIVATEKEY", true)]
    [InlineData("evidgate__github__clientsecret", true)]
    [InlineData("EVIDGATE__GITHUB__APPID", true)]
    [InlineData("EVIDGATE__OAUTH__GITHUB__CLIENTSECRET", false)]
    [InlineData("EVIDGATE__OIDC__GOOGLE__CLIENTSECRET", false)]
    public void IsAppKey_SeparatesAppSettingsFromLoginSettings(string name, bool expected)
    {
        IsAppKey(name).ShouldBe(expected);
    }

    private static bool IsAppKey(string name) => name.StartsWith(AppPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every string value of a property named <paramref name="property"/>, at any depth of the document.</summary>
    private static List<string> NamesIn(string relativePath, string property)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(SolutionLayout.RepoRoot, relativePath)));
        var found = new List<string>();
        Collect(document.RootElement, property, found);
        found.Count.ShouldBeGreaterThan(5, $"{relativePath} was not read");

        return found;
    }

    private static void Collect(JsonElement element, string property, List<string> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    if (member.Name == property && member.Value.ValueKind == JsonValueKind.String)
                    {
                        found.Add(member.Value.GetString()!);
                    }

                    Collect(member.Value, property, found);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, property, found);
                }

                break;
        }
    }
}
