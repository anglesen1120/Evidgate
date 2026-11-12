using System.Diagnostics;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Persistence.Secrets;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// <c>scripts/env/secret-placeholders.mjs</c> against a temp copy of the template and the
/// inventory: a clean checkout resolves every required secret through the real
/// <see cref="ISecretProvider"/>, a rerun changes nothing, no value reaches the output and an
/// inventory entry without a rule fails before anything is written.
/// </summary>
public sealed class SecretPlaceholderGeneratorTests : IDisposable
{
    private static readonly string Script = Path.Combine(ContractPaths.RepoRoot, "scripts", "env", "secret-placeholders.mjs");

    private readonly string _root = Directory.CreateTempSubdirectory("evidgate-secret-placeholders-").FullName;

    public SecretPlaceholderGeneratorTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "infra"));
        File.Copy(Path.Combine(ContractPaths.RepoRoot, ".env.dev.template"), Path.Combine(_root, ".env.dev.template"));
        foreach (var name in new[] { "secrets-inventory.json", "config-matrix.json" })
        {
            File.Copy(Path.Combine(ContractPaths.RepoRoot, "infra", name), Path.Combine(_root, "infra", name));
        }
    }

    private string EnvFile => Path.Combine(_root, ".env.dev");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task FreshDev_ResolvesEveryGeneratedSecretThroughTheSecretProvider()
    {
        (await RunAsync("dev")).ExitCode.ShouldBe(0);
        var values = ReadEnvFile(EnvFile);
        var inventory = Rules();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair => KeyValuePair.Create<string, string?>(
                SecretNames.ToConfigurationKey(pair.Key), pair.Value)))
            .Build();
        using var provider = new ServiceCollection().AddEvidgateSecrets(configuration).BuildServiceProvider();
        var secrets = provider.GetRequiredService<ISecretProvider>();

        foreach (var name in SecretNames.All)
        {
            inventory.ShouldContainKey(name, $"{name} has no inventory entry");
            if (inventory[name] != "not-generated")
            {
                (await secrets.GetSecretAsync(name, TestContext.Current.CancellationToken)).ShouldNotBeNullOrWhiteSpace($"{name} did not resolve on a fresh boot");
            }
        }

        foreach (var name in new[] { SecretNames.CiPepper, SecretNames.CiSigningKey, SecretNames.SessionPepper, SecretNames.InvitePepper })
        {
            Convert.FromBase64String((await secrets.GetSecretAsync(name, TestContext.Current.CancellationToken))!).Length.ShouldBeGreaterThanOrEqualTo(32);
        }
    }

    [Fact]
    public async Task FreshDev_DefinesEveryKeyTheMatrixRequiresInDev_ExceptTheExportedSeaweedConfig()
    {
        (await RunAsync("dev")).ExitCode.ShouldBe(0);
        var values = ReadEnvFile(EnvFile);
        var rules = Rules();
        var matrix = JsonNode.Parse((await File.ReadAllTextAsync(Path.Combine(_root, "infra", "config-matrix.json"), TestContext.Current.CancellationToken)))!;

        var required = Walk(matrix)
            .Where(row => row["secret"]?.GetValue<bool>() == true && row["required"]?["dev"]?.GetValue<bool>() == true)
            .Select(row => row["key"]!.GetValue<string>())
            .ToList();

        required.ShouldNotBeEmpty();
        foreach (var key in required.Where(key => rules[key] != "not-generated"))
        {
            values.ShouldContainKey(key);
            values[key].ShouldNotBeNullOrWhiteSpace(key);
        }

        required.Where(key => rules[key] == "not-generated").ShouldBe(["EVIDGATE_SEAWEEDFS_S3_CONFIG"]);
    }

    [Fact]
    public async Task SecondRun_ChangesNothing()
    {
        (await RunAsync("dev")).ExitCode.ShouldBe(0);
        var before = (await File.ReadAllBytesAsync(EnvFile, TestContext.Current.CancellationToken));

        var second = await RunAsync("dev");

        second.ExitCode.ShouldBe(0);
        second.Output.ShouldContain("nothing to add");
        (await File.ReadAllBytesAsync(EnvFile, TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    [Fact]
    public async Task ExistingValues_AreNeverOverwritten_AndOnlyMissingKeysAreAdded()
    {
        await File.WriteAllTextAsync(EnvFile, "EVIDGATE_APP_PASSWORD=kept-by-the-developer\nEVIDGATE__CI__PEPPER=\n", TestContext.Current.CancellationToken);

        var run = await RunAsync("dev");

        run.ExitCode.ShouldBe(0, run.Output);
        var values = ReadEnvFile(EnvFile);
        values["EVIDGATE_APP_PASSWORD"].ShouldBe("kept-by-the-developer");
        values["EVIDGATE__CI__PEPPER"].ShouldNotBeNullOrWhiteSpace();
        values["EVIDGATE__DATABASE__CONNECTIONSTRING"].ShouldContain("Password=kept-by-the-developer");
    }

    [Fact]
    public async Task Output_NeverCarriesAGeneratedValue()
    {
        var first = await RunAsync("dev");
        var second = await RunAsync("dev");
        var staging = await RunAsync("staging");

        foreach (var value in ReadEnvFile(EnvFile).Values.Where(value => value.Length >= 16))
        {
            foreach (var run in new[] { first, second, staging })
            {
                run.Output.ShouldNotContain(value);
            }
        }

        first.Output.ShouldContain("EVIDGATE__CI__PEPPER");
    }

    [Fact]
    public async Task InventoryEntryWithoutARule_FailsLoudly_AndWritesNothing()
    {
        var path = Path.Combine(_root, "infra", "secrets-inventory.json");
        var inventory = JsonNode.Parse((await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)))!;
        inventory["secrets"]!.AsArray().First(entry => entry!["name"]!.GetValue<string>() == "EVIDGATE__CI__PEPPER")!
            .AsObject().Remove("generator");
        await File.WriteAllTextAsync(path, inventory.ToJsonString(), TestContext.Current.CancellationToken);

        foreach (var env in new[] { "dev", "staging" })
        {
            var run = await RunAsync(env);

            run.ExitCode.ShouldNotBe(0);
            run.Output.ShouldContain("EVIDGATE__CI__PEPPER: no generator rule");
        }

        File.Exists(EnvFile).ShouldBeFalse();
        Directory.Exists(Path.Combine(_root, ".secrets")).ShouldBeFalse();
    }

    [Fact]
    public async Task Staging_WritesEmptyNamedEntries_KeepsExistingOnes_AndRerunsClean()
    {
        var file = Path.Combine(_root, ".secrets", "staging.placeholders.env");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "EVIDGATE__CI__PEPPER=filled-in-the-store\n", TestContext.Current.CancellationToken);

        (await RunAsync("staging")).ExitCode.ShouldBe(0);
        var values = ReadEnvFile(file);
        var bytes = (await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));

        values["EVIDGATE__CI__PEPPER"].ShouldBe("filled-in-the-store");
        values["EVIDGATE__OIDC__GOOGLE__CLIENTSECRET"].ShouldBeEmpty();
        values.ShouldNotContainKey("EVIDGATE_APP_PASSWORD");

        (await RunAsync("staging")).Output.ShouldContain("nothing to add");
        (await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken)).ShouldBe(bytes);
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string env)
    {
        var start = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { Script, "--env", env, "--root", _root })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(cancel.Token);
        return (process.ExitCode, await output + await error);
    }

    private Dictionary<string, string> Rules() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "infra", "secrets-inventory.json")))!["secrets"]!
            .AsArray()
            .ToDictionary(entry => entry!["name"]!.GetValue<string>(), entry => entry!["generator"]!["kind"]!.GetValue<string>());

    private static Dictionary<string, string> ReadEnvFile(string path) =>
        File.ReadAllLines(path)
            .Where(line => line.Contains('=') && !line.TrimStart().StartsWith('#'))
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim().Trim('\''));

    private static IEnumerable<JsonObject> Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array.Where(item => item is not null))
                {
                    foreach (var row in Walk(item!)) { yield return row; }
                }

                break;
            case JsonObject obj:
                if (obj["key"] is JsonValue && obj["secret"] is not null)
                {
                    yield return obj;
                }
                else
                {
                    foreach (var child in obj.Select(pair => pair.Value).Where(child => child is not null))
                    {
                        foreach (var row in Walk(child!)) { yield return row; }
                    }
                }

                break;
        }
    }
}
