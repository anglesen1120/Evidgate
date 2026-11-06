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
/// is re-runnable, verify reads the committed lifecycle rule back and refuses an
/// anonymous read, and the production presigner uploads into the bucket the
/// script created. The staging/prod public-access block is not exercised here:
/// the emulator has no such sub-resource, and a real cloud is [OPEN] until the
/// provider is picked (infra/README.md, "Bucket bootstrap").
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
            $"the presigned PUT must be accepted; status={(int)response.StatusCode}");

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
        // Built at run time: a literal that looks like a credential would trip the
        // repository's secret-literal scan for no reason.
        var wrongSecret = $"wrong-{Guid.NewGuid():N}-credential";

        var result = await RunScriptAsync(
            "dev",
            "verify",
            FreshBucketName(),
            ("EVIDGATE__OBJECT__SECRETKEY", wrongSecret));

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldNotContain(wrongSecret);
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
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] =
            Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";
        foreach (var (name, value) in variables)
        {
            if (value is not null)
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start bootstrap-bucket.sh");
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
                $"bootstrap-bucket.sh {environment} {action} did not finish within {ScriptTimeout.TotalSeconds}s");
        }

        var output = await stdout;
        output += await stderr;

        return new ScriptResult(process.ExitCode, output);
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
