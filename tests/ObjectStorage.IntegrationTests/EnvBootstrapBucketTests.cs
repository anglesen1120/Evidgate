using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Evidgate.BuildingBlocks.ObjectStorage;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace ObjectStorage.IntegrationTests;

/// <summary>
/// The per-environment bucket bootstrap against the SeaweedFS fixture: provision
/// is re-runnable, verify requires one enabled <c>pending/</c> 1-day lifecycle
/// rule and proves a canary object it stores is refused to an unsigned reader,
/// and the production presigner uploads into the bucket the script created.
/// A GetObject-only bucket policy is how a real deployment can make a stored
/// object public, so a test publishes one and requires verify to refuse; the
/// emulator's block-public-access sub-resource stays untested, and
/// infra/README.md records that it is verified only on a real cloud.
/// </summary>
[Collection(SeaweedFsCollectionDefinition.Name)]
[Trait("Category", "Docker")]
public sealed class EnvBootstrapBucketTests(SeaweedFsFixture fixture) : IDisposable
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(180);

    private readonly HttpClient _http = new();

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task Provision_Twice_Then_Verify_Succeeds_And_Never_Prints_The_Secret()
    {
        var bucket = FreshBucketName();

        var first = await RunScriptAsync("dev", "provision", bucket);
        first.ExitCode.ShouldBe(0, first.Describe());

        var second = await RunScriptAsync("dev", "provision", bucket);
        second.ExitCode.ShouldBe(0, second.Describe());

        var verify = await RunScriptAsync("dev", "verify", bucket);
        verify.ExitCode.ShouldBe(0, verify.Describe());

        foreach (var result in new[] { first, second, verify })
        {
            result.Output.ShouldNotContain(fixture.Target.SecretKey);
        }
    }

    [Fact]
    public async Task Presigned_Put_Into_A_Provisioned_Bucket_Stores_The_Bytes_And_Anonymous_Read_Is_Refused()
    {
        var bucket = FreshBucketName();
        var provision = await RunScriptAsync("dev", "provision", bucket);
        provision.ExitCode.ShouldBe(0, provision.Describe());

        var body = RandomNumberGenerator.GetBytes(2048);
        var key = $"pending/smoke-{Guid.NewGuid():D}";
        using var adapter = Adapter(bucket);

        var presigned = adapter.PresignPut(new PresignPutRequest(key, body.LongLength, Sha256Hex(body)));
        using var response = await PutAsync(presigned, body);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"the presigned PUT must be accepted; status={(int)response.StatusCode} body={await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");

        var cancellationToken = TestContext.Current.CancellationToken;
        (await adapter.GetAsync(key, body.LongLength, cancellationToken)).ShouldBe(body);

        using var anonymous = await _http.GetAsync(
            new Uri($"{fixture.Target.ServiceUrl.TrimEnd('/')}/{bucket}/{key}"),
            cancellationToken);
        var anonymousStatus = (int)anonymous.StatusCode;
        (anonymousStatus is 401 or 403).ShouldBeTrue(
            $"an unsigned GET of a stored object must be refused; status={anonymousStatus}");
    }

    [Fact]
    public async Task Provision_Without_The_Secret_Key_Fails_Naming_The_Key()
    {
        var result = await RunScriptAsync(
            "prod",
            "provision",
            "evidgate-bootstrap-missing-secret",
            ("EVIDGATE_BOOTSTRAP_CONFIRM_ENV", "prod"),
            ("EVIDGATE__OBJECT__SECRETKEY", null));

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("EVIDGATE__OBJECT__SECRETKEY");
        // The cleared environment is what proves the fixture's own credential
        // cannot reach the child.
        result.Output.ShouldNotContain(fixture.Target.SecretKey);
    }

    [Fact]
    public async Task Provision_In_Prod_Without_The_Confirmation_Refuses()
    {
        var result = await RunScriptAsync(
            "prod",
            "provision",
            "evidgate-bootstrap-confirm",
            ("EVIDGATE_BOOTSTRAP_CONFIRM_ENV", null));

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("EVIDGATE_BOOTSTRAP_CONFIRM_ENV");
    }

    [Fact]
    public async Task Verify_On_A_Bucket_That_Does_Not_Exist_Fails()
    {
        var result = await RunScriptAsync("dev", "verify", FreshBucketName());

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldNotContain(fixture.Target.SecretKey);
    }

    [Fact]
    public async Task Verify_With_A_Wrong_Secret_Fails_Without_Printing_The_Secret()
    {
        var bucket = FreshBucketName();
        var provision = await RunScriptAsync("dev", "provision", bucket);
        provision.ExitCode.ShouldBe(0, provision.Describe());

        // The positive control: with the fixture's own secret the same bucket
        // verifies, so the refusal below can only come from the wrong secret.
        var control = await RunScriptAsync("dev", "verify", bucket);
        control.ExitCode.ShouldBe(0, control.Describe());

        // Built at run time: a literal that looks like a credential would trip the
        // repository's secret-literal scan for no reason.
        var wrongSecret = $"wrong-{Guid.NewGuid():N}-credential";

        var result = await RunScriptAsync(
            "dev",
            "verify",
            bucket,
            ("EVIDGATE__OBJECT__SECRETKEY", wrongSecret));

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldNotContain(wrongSecret);
        result.Output.ShouldNotContain(fixture.Target.SecretKey);
    }

    [Fact]
    public async Task Verify_With_A_Disabled_Pending_Rule_Fails_On_The_Rule()
    {
        var bucket = FreshBucketName();
        var provision = await RunScriptAsync("dev", "provision", bucket);
        provision.ExitCode.ShouldBe(0, provision.Describe());

        var control = await RunScriptAsync("dev", "verify", bucket);
        control.ExitCode.ShouldBe(0, control.Describe());

        var put = await SignedCurlAsync(
            "PUT",
            $"{fixture.Target.ServiceUrl.TrimEnd('/')}/{bucket}?lifecycle=",
            LifecycleDocument(PendingRule("Disabled", "pending/", "1")));
        put.ExitCode.ShouldBe(0, put.Describe());
        put.Output.Trim().ShouldBe("200", put.Describe());

        var result = await RunScriptAsync("dev", "verify", bucket);
        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("lifecycle");
        result.Output.ShouldNotContain(fixture.Target.SecretKey);
    }

    [Fact]
    public async Task Verify_Refuses_A_Canary_Published_By_A_GetObject_Bucket_Policy()
    {
        var bucket = FreshBucketName();
        var provision = await RunScriptAsync("dev", "provision", bucket);
        provision.ExitCode.ShouldBe(0, provision.Describe());

        var control = await RunScriptAsync("dev", "verify", bucket);
        control.ExitCode.ShouldBe(0, control.Describe());

        // GetObject only, on the prefix verify stores its canary under: the
        // bucket stays unlistable, the stored object becomes readable by anyone.
        var policy =
            $$"""
            {"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":"*","Action":"s3:GetObject","Resource":"arn:aws:s3:::{{bucket}}/bootstrap-verify/*"}]}
            """;
        var putPolicy = await SignedCurlAsync(
            "PUT",
            $"{fixture.Target.ServiceUrl.TrimEnd('/')}/{bucket}?policy=",
            policy);
        putPolicy.ExitCode.ShouldBe(0, putPolicy.Describe());
        int.Parse(putPolicy.Output.Trim(), System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBeInRange(200, 299, putPolicy.Describe());

        // Proof the policy took effect on an object of ours, so a later pass of
        // verify cannot be explained away as "the emulator ignored the policy".
        var probeKey = $"bootstrap-verify/published-{Guid.NewGuid():N}";
        var probeUrl = $"{fixture.Target.ServiceUrl.TrimEnd('/')}/{bucket}/{probeKey}";
        var putProbe = await SignedCurlAsync("PUT", probeUrl, "published canary");
        putProbe.Output.Trim().ShouldBe("200", putProbe.Describe());
        using var anonymous = await _http.GetAsync(
            new Uri(probeUrl), TestContext.Current.CancellationToken);
        ((int)anonymous.StatusCode).ShouldBe(
            200, "the bucket policy must publish this key, or verify cannot detect a public canary");

        var result = await RunScriptAsync("dev", "verify", bucket);
        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("anonymous");
        result.Output.ShouldNotContain(fixture.Target.SecretKey);
    }

    [Fact]
    public async Task Verify_In_Staging_With_An_Http_Endpoint_Is_Refused()
    {
        var result = await RunScriptAsync(
            "staging",
            "verify",
            FreshBucketName(),
            ("EVIDGATE__OBJECT__ENDPOINT", "http://127.0.0.1:1"));

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("https");
        result.Output.ShouldNotContain(fixture.Target.SecretKey);
    }

    [Fact]
    public async Task Bootstrap_With_Endpoint_Userinfo_Is_Refused_Without_Printing_It()
    {
        var password = $"s3cr3t-{Guid.NewGuid():N}";
        var userInfo = "bootstrap-user:" + password;

        var result = await RunScriptAsync(
            "dev",
            "verify",
            FreshBucketName(),
            ("EVIDGATE__OBJECT__ENDPOINT", "http://" + userInfo + "@127.0.0.1:8333"));

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("credentials");
        result.Output.ShouldNotContain(password);
        result.Output.ShouldNotContain(fixture.Target.SecretKey);
    }

    [Fact]
    public async Task Bootstrap_With_Endpoint_Starting_With_A_Dash_Is_Refused()
    {
        var result = await RunScriptAsync(
            "dev",
            "verify",
            FreshBucketName(),
            ("EVIDGATE__OBJECT__ENDPOINT", "-o/tmp/evidgate-bootstrap-endpoint"));

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("must not start with");
        result.Output.ShouldNotContain(fixture.Target.SecretKey);
    }

    private async Task<ScriptResult> RunScriptAsync(
        string environment,
        string action,
        string bucket,
        params (string Name, string? Value)[] overrides)
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE__OBJECT__BUCKET"] = bucket,
            ["EVIDGATE__OBJECT__ENDPOINT"] = fixture.Target.ServiceUrl,
            ["EVIDGATE__OBJECT__REGION"] = fixture.Target.Region,
            ["EVIDGATE__OBJECT__ACCESSKEY"] = fixture.Target.AccessKey,
            ["EVIDGATE__OBJECT__SECRETKEY"] = fixture.Target.SecretKey,
        };
        foreach (var (name, value) in overrides)
        {
            variables[name] = value;
        }

        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot(),
        };
        startInfo.ArgumentList.Add(Path.Combine(RepositoryRoot(), "scripts", "env", "bootstrap-bucket.sh"));
        startInfo.ArgumentList.Add(environment);
        startInfo.ArgumentList.Add(action);

        // A cleared environment: an inherited EVIDGATE_* value must not reach the
        // script, or a failing case could pass on the machine's own configuration.
        ClearEnvironment(startInfo);
        foreach (var (name, value) in variables)
        {
            if (value is not null)
            {
                startInfo.Environment[name] = value;
            }
        }

        return await ExecuteAsync(startInfo, null, $"bootstrap-bucket.sh {environment} {action}");
    }

    /// <summary>
    /// Sends one signed curl request and returns the HTTP status it printed: the
    /// credentials ride a configuration on stdin, so they never reach the
    /// process arguments.
    /// </summary>
    private async Task<ScriptResult> SignedCurlAsync(string method, string url, string? body)
    {
        var startInfo = new ProcessStartInfo("curl")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        ClearEnvironment(startInfo);
        foreach (var argument in new[]
        {
            "--config", "-",
            "--request", method,
            "--silent", "--show-error",
            "--output", "/dev/null",
            "--write-out", "%{http_code}",
            url,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var config =
            $"aws-sigv4 = \"aws:amz:{fixture.Target.Region}:s3\"\n" +
            $"user = \"{fixture.Target.AccessKey}:{fixture.Target.SecretKey}\"\n";
        if (body is not null)
        {
            config += $"data-binary = \"{CurlConfigValue(body)}\"\n";
        }

        return await ExecuteAsync(startInfo, config, $"{method} {url}");
    }

    /// <summary>
    /// Escapes a value for a curl configuration line: an unescaped quote or
    /// backslash would end or alter the value, and a JSON policy carries both.
    /// </summary>
    private static string CurlConfigValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static async Task<ScriptResult> ExecuteAsync(
        ProcessStartInfo startInfo,
        string? standardInput,
        string label)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"could not start {label}");
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), CancellationToken.None);
            await process.StandardInput.FlushAsync(CancellationToken.None);
            process.StandardInput.Close();
        }

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
                $"{label} did not finish within {ScriptTimeout.TotalSeconds}s");
        }

        var output = await stdout;
        output += await stderr;

        return new ScriptResult(process.ExitCode, output);
    }

    private static string LifecycleDocument(string rules) =>
        $"<LifecycleConfiguration>{rules}</LifecycleConfiguration>";

    private static string PendingRule(string status, string prefix, string days) =>
        $"<Rule><ID>evidgate-pending-24h</ID><Filter><Prefix>{prefix}</Prefix></Filter>" +
        $"<Status>{status}</Status><Expiration><Days>{days}</Days></Expiration></Rule>";

    // A cleared environment: an inherited EVIDGATE_*, proxy or S3 setting must
    // not reach the child, or a failing case could pass on the machine's own
    // configuration and a passing one could depend on it.
    private static void ClearEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] =
            Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";
    }

    private S3ObjectStorage Adapter(string bucket) => new(Options.Create(new ObjectStorageOptions
    {
        Endpoint = fixture.Target.ServiceUrl,
        Region = fixture.Target.Region,
        Bucket = bucket,
        ForcePathStyle = fixture.Target.ForcePathStyle,
        PresignExpirySeconds = 900,
        AccessKey = fixture.Target.AccessKey,
        SecretKey = fixture.Target.SecretKey,
    }));

    private async Task<HttpResponseMessage> PutAsync(PresignedPut presigned, byte[] body)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, presigned.Url)
        {
            Content = new ByteArrayContent(body),
        };
        foreach (var (name, value) in presigned.RequiredHeaders)
        {
            // The content sets the length; the signature covers the value that
            // matches this body.
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            message.Content.Headers.TryAddWithoutValidation(name, value);
        }

        return await _http.SendAsync(message);
    }

    private static string FreshBucketName() =>
        "evidgate-bootstrap-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private static string Sha256Hex(byte[] body) => Convert.ToHexStringLower(SHA256.HashData(body));

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
