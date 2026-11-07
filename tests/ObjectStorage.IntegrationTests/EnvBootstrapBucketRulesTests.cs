using System.Diagnostics;
using Shouldly;
using Xunit;

namespace ObjectStorage.IntegrationTests;

/// <summary>
/// The rule predicates <c>scripts/env/lib-bucket.sh</c> adds for the environment
/// bootstrap: the enabled <c>pending/</c> 1-day lifecycle rule that must sit in
/// one rule, and the AWS regional host name that alone takes the region in a
/// CreateBucket body. They read files and strings only, so no container is
/// needed and every branch runs here.
/// </summary>
public sealed class EnvBootstrapBucketRulesTests
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task Lifecycle_Matcher_Requires_One_Rule_That_Is_Enabled_Pending_And_One_Day()
    {
        var cases = new (string Name, string Document, bool Matches)[]
        {
            ("enabled", LifecycleDocument(PendingRule("Enabled", "pending/", "1")), true),
            (
                "indented-multi-line-echo",
                """
                <LifecycleConfiguration>
                  <Rule>
                    <ID>evidgate-pending-24h</ID>
                    <Filter>
                      <Prefix>pending/</Prefix>
                    </Filter>
                    <Status>Enabled</Status>
                    <Expiration>
                      <Days>1</Days>
                    </Expiration>
                  </Rule>
                </LifecycleConfiguration>
                """,
                true),
            ("disabled", LifecycleDocument(PendingRule("Disabled", "pending/", "1")), false),
            ("other-prefix", LifecycleDocument(PendingRule("Enabled", "tmp/", "1")), false),
            ("seven-days", LifecycleDocument(PendingRule("Enabled", "pending/", "7")), false),
            (
                "predicates-split-across-rules",
                LifecycleDocument(
                    PendingRule("Disabled", "pending/", "1") + PendingRule("Enabled", "other/", "1")),
                false),
            (
                "our-rule-after-another",
                LifecycleDocument(
                    PendingRule("Enabled", "other/", "7") + PendingRule("Enabled", "pending/", "1")),
                true),
        };

        foreach (var @case in cases)
        {
            var file = Path.Combine(Path.GetTempPath(), $"evidgate-lifecycle-{Guid.NewGuid():N}.xml");
            await File.WriteAllTextAsync(file, @case.Document, TestContext.Current.CancellationToken);
            try
            {
                var result = await RunBashAsync(
                    ". scripts/env/lib-bucket.sh && evg_s3_lifecycle_rule_enabled_pending_1day \"$1\"",
                    file);
                (result.ExitCode == 0).ShouldBe(@case.Matches, $"{@case.Name}: {result.Describe()}");
            }
            finally
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task Aws_Host_Matcher_Matches_Only_Regional_Amazon_Host_Names()
    {
        var cases = new (string Endpoint, bool Aws)[]
        {
            ("https://s3.eu-west-1.amazonaws.com", true),
            ("https://s3.eu-west-1.amazonaws.com:443", true),
            ("http://127.0.0.1:8333", false),
            ("https://amazonaws.com", false),
            ("https://s3.example.com", false),
        };

        foreach (var @case in cases)
        {
            var result = await RunBashAsync(". scripts/env/lib-bucket.sh && evg_s3_aws_host \"$1\"", @case.Endpoint);
            (result.ExitCode == 0).ShouldBe(@case.Aws, $"{@case.Endpoint}: {result.Describe()}");
        }
    }

    private static string LifecycleDocument(string rules) =>
        $"<LifecycleConfiguration>{rules}</LifecycleConfiguration>";

    private static string PendingRule(string status, string prefix, string days) =>
        $"<Rule><ID>evidgate-pending-24h</ID><Filter><Prefix>{prefix}</Prefix></Filter>" +
        $"<Status>{status}</Status><Expiration><Days>{days}</Days></Expiration></Rule>";

    private static async Task<ScriptResult> RunBashAsync(string script, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot(),
        };
        // A cleared environment: an inherited EVIDGATE_* value must not reach the
        // sourced script, or a case could pass on the machine's own configuration.
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] =
            Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("evidgate-bucket-rules-test");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"could not start bash -c {script}");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(ScriptTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                $"bash -c {script} did not finish within {ScriptTimeout.TotalSeconds}s");
        }

        var output = await stdout;
        output += await stderr;

        return new ScriptResult(process.ExitCode, output);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Evidgate.slnx not found above {AppContext.BaseDirectory}");
    }

    private sealed record ScriptResult(int ExitCode, string Output)
    {
        public string Describe() => $"exit={ExitCode}{Environment.NewLine}{Output}";
    }
}
