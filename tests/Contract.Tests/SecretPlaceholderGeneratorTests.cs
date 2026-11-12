using System.Diagnostics;
using System.Runtime.Versioning;
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
        values["EVIDGATE__DATABASE__CONNECTIONSTRING"].Split(';').Select(part => part.Split('=', 2))
            .First(part => part[0] == "Password")[1].ShouldBe(values["EVIDGATE_APP_PASSWORD"]);
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
        File.Exists(Path.Combine(_root, ".env.staging.secrets")).ShouldBeFalse();
    }

    [Fact]
    public async Task Staging_WritesEmptyNamedEntries_KeepsExistingOnes_AndRerunsClean()
    {
        var file = Path.Combine(_root, ".env.staging.secrets");
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

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task FreshAndNoOpRuns_LeaveTheFilePrivate_EvenWhenItWasWorldReadable()
    {
        (await RunAsync("dev")).ExitCode.ShouldBe(0);
        File.GetUnixFileMode(EnvFile).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);

        File.SetUnixFileMode(EnvFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        (await RunAsync("dev")).Output.ShouldContain("nothing to add");

        File.GetUnixFileMode(EnvFile).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task SparseOlderFile_TakesTheCommittedLocalIdentityFromTheTemplate()
    {
        await File.WriteAllTextAsync(EnvFile, "EVIDGATE_DB_PORT=5432\n", TestContext.Current.CancellationToken);

        (await RunAsync("dev")).ExitCode.ShouldBe(0);

        var values = ReadEnvFile(EnvFile);
        var template = ReadEnvFile(Path.Combine(_root, ".env.dev.template"));
        values["EVIDGATE__OBJECT__ACCESSKEY"].ShouldBe(template["EVIDGATE__OBJECT__ACCESSKEY"]);
        values["EVIDGATE_S3_SECRET_KEY"].ShouldBe(template["EVIDGATE_S3_SECRET_KEY"]);
    }

    [Fact]
    public async Task IndentedAndDuplicateAssignments_AreReadTheWayTheShellSourcesThem()
    {
        await File.WriteAllTextAsync(
            EnvFile,
            "  EVIDGATE__SESSION__PEPPER=indented-and-kept\nEVIDGATE__INVITE__PEPPER=\nEVIDGATE__INVITE__PEPPER=\n",
            TestContext.Current.CancellationToken);

        (await RunAsync("dev")).ExitCode.ShouldBe(0);

        var lines = await File.ReadAllLinesAsync(EnvFile, TestContext.Current.CancellationToken);
        lines.Count(line => line.Contains("EVIDGATE__SESSION__PEPPER=", StringComparison.Ordinal)).ShouldBe(1);
        ReadEnvFile(EnvFile)["EVIDGATE__SESSION__PEPPER"].ShouldBe("indented-and-kept");
        ReadEnvFile(EnvFile)["EVIDGATE__INVITE__PEPPER"].ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExistingPasswordWithSeparatorAndQuote_ProducesAShellSafeQuotedConnectionString()
    {
        await File.WriteAllTextAsync(EnvFile, "EVIDGATE_APP_PASSWORD=\"a;b'c\"\n", TestContext.Current.CancellationToken);

        (await RunAsync("dev")).ExitCode.ShouldBe(0);

        var shell = await RunShellAsync($"set -a; . '{EnvFile}'; printf '%s' \"$EVIDGATE__DATABASE__CONNECTIONSTRING\"");
        shell.ShouldEndWith("\"a;b'c\"");
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task SymlinkedTarget_IsRefused_AndItsDestinationIsNotTouched()
    {
        var real = Path.Combine(_root, "real.env");
        await File.WriteAllTextAsync(real, "EVIDGATE_DB_PORT=5432\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        File.CreateSymbolicLink(EnvFile, real);

        var run = await RunAsync("dev");

        run.ExitCode.ShouldNotBe(0);
        run.Output.ShouldContain("is a symlink");
        File.GetUnixFileMode(real).HasFlag(UnixFileMode.GroupRead).ShouldBeTrue();
        (await File.ReadAllTextAsync(real, TestContext.Current.CancellationToken)).ShouldBe("EVIDGATE_DB_PORT=5432\n");
    }

    [Fact]
    public async Task HeldLock_RefusesTheRun_AndLeavesTheFileAlone()
    {
        Directory.CreateDirectory(EnvFile + ".lock");

        var run = await RunAsync("dev");

        run.ExitCode.ShouldNotBe(0);
        run.Output.ShouldContain("another run is in progress");
        File.Exists(EnvFile).ShouldBeFalse();
    }

    private static async Task<string> RunShellAsync(string script)
    {
        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return output;
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
        try
        {
            await process.WaitForExitAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await output + await error);
    }

    private Dictionary<string, string> Rules() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "infra", "secrets-inventory.json")))!["secrets"]!
            .AsArray()
            .ToDictionary(entry => entry!["name"]!.GetValue<string>(), entry => entry!["generator"]!["kind"]!.GetValue<string>());

    // Last assignment wins, as when the shell sources the file.
    private static Dictionary<string, string> ReadEnvFile(string path)
    {
        var values = new Dictionary<string, string>();
        foreach (var line in File.ReadAllLines(path).Where(line => line.Contains('=') && !line.TrimStart().StartsWith('#')))
        {
            var parts = line.Split('=', 2);
            values[parts[0].Trim()] = parts[1].Trim().Trim('\'');
        }

        return values;
    }

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
