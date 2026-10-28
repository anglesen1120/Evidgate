// 06.1.2-T2: the real SeaweedFS S3 container the end-to-end `complete` tests PUT bytes to and
// inspect afterwards. Only the container-start logic of tests/ObjectStorage.IntegrationTests'
// SeaweedFsFixture is copied (the digest-pinned image, the mounted local-only identity file, the
// S3 gateway command and the HTTP readiness probe); the lifecycle-rule half of that fixture
// belongs to 01.8.1-T1 (AC7) and is not needed here.
using System.Net;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// One digest-pinned SeaweedFS S3 gateway for the 06.1.2-T2 end-to-end suite (a class fixture, so a
/// test class starts it once). The bucket is created before any test runs, the credentials are read
/// from the same committed local-only identity file the dev stack mounts (never a literal), and the
/// endpoint/bucket/credentials are handed to the host through the same in-memory configuration seam
/// the 06.1.2-T1 tests use, pointed at this container.
/// </summary>
public sealed class SeaweedFsStore : IAsyncLifetime
{
    internal const int S3Port = 8333;

    private const string ContainerConfigPath = "/etc/seaweedfs/s3-config.json";
    private const string Region = "us-east-1";

    /// <summary>
    /// How long a freshly started gateway may keep refusing its first writes before the fixture gives up. A
    /// healthy start accepts the first write within ~4 s (measured on the reference host), so the budget is
    /// only spent on a start that really is unhealthy; it bounds the extra wall time instead of retrying
    /// without limit.
    /// </summary>
    private static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(120);

    /// <summary>The probe object of the warm-up (written and deleted inside the retry loop).</summary>
    private const string WarmUpKey = "warm-up/probe";

    /// <summary>The bucket every 06.1.2-T2 test uploads into (created before the first test).</summary>
    internal const string BucketName = "evidgate-evidence-complete";

    private readonly FileInfo _configFile = new(
        Path.Combine(RepositoryRoot(), "infra", "seaweedfs", "s3-config.json"));

    private IContainer? _container;

    /// <summary>The container's S3 endpoint, e.g. <c>http://127.0.0.1:49153</c>.</summary>
    internal string Endpoint { get; private set; } = null!;

    internal string AccessKey { get; private set; } = null!;

    internal string SecretKey { get; private set; } = null!;

    /// <summary>
    /// The <c>EVIDGATE:Object</c> configuration the API host binds — the same keys the 06.1.2-T1
    /// suite uses, with the endpoint and the credentials of this container. Rebuilt per call so a
    /// host always sees the running container's mapped port.
    /// </summary>
    internal Dictionary<string, string?> Configuration() => new(StringComparer.Ordinal)
    {
        ["EVIDGATE:Object:Bucket"] = BucketName,
        ["EVIDGATE:Object:Region"] = Region,
        ["EVIDGATE:Object:Endpoint"] = Endpoint,
        ["EVIDGATE:Object:ForcePathStyle"] = "true",
        ["EVIDGATE:OBJECT:ACCESSKEY"] = AccessKey,
        ["EVIDGATE:OBJECT:SECRETKEY"] = SecretKey,
    };

    public async ValueTask InitializeAsync()
    {
        var identity = ReadFirstIdentity(_configFile);

        _container = new ContainerBuilder(ContainerImages.SeaweedFs)
            .WithResourceMapping(_configFile, new FileInfo(ContainerConfigPath))
            .WithCommand("server", "-s3", $"-s3.port={S3Port}", $"-s3.config={ContainerConfigPath}")
            .WithPortBinding(S3Port, assignRandomHostPort: true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(
                        request => request.ForPort(S3Port).ForPath("/").ForStatusCodeMatching(_ => true)))
            .Build();

        await _container.StartAsync();

        Endpoint = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(S3Port)}";
        AccessKey = identity.AccessKey;
        SecretKey = identity.SecretKey;

        // Both first S3 calls of a fresh gateway run under this retry, and until F59 neither was protected:
        // the bucket creation had no retry at all and the warm-up that followed retried only
        // AmazonServiceException. The gateway's own start window is real — its mapped port answers any HTTP
        // status (a 500 included) before the master's raft leader has settled, which the container log shows
        // as "raft.Server: Not current leader", and a caller before it serves gets a reset connection — so one
        // transient 500 or reset there failed every case of the class at fixture init (12/12 in the locked
        // gate). Retrying both keeps every later write ordinary.
        await RetryWhileStartingAsync(async () =>
        {
            await CreateBucketAsync();
            await WarmUpAsync();
        });
    }

    /// <summary>
    /// Runs <paramref name="step"/> until it succeeds, retrying only what a starting SeaweedFS produces: a
    /// 5xx or 408 answer (the gateway's "S3 internal error", the master's "Not current leader" churn) and a
    /// connection-level failure (the mapped port answers at TCP level before the gateway serves). An auth,
    /// 404 or other 4xx answer is a real fault and is rethrown at once, so a wrong credential or a missing
    /// bucket is never retried away. When the budget is spent the last error is rethrown with the
    /// container's log tail, so the next flake carries its own cause instead of a bare 500.
    /// </summary>
    private async Task RetryWhileStartingAsync(Func<Task> step)
    {
        var deadline = DateTime.UtcNow + StartupBudget;

        while (true)
        {
            try
            {
                await step();
                return;
            }
            catch (Exception exception) when (IsTransientStartupFailure(exception))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new InvalidOperationException(
                        "the SeaweedFS store refused its first writes for "
                        + $"{(int)StartupBudget.TotalSeconds}s after the container started (last error: "
                        + $"{exception.GetType().Name}: {exception.Message}). Container log tail:"
                        + $"{Environment.NewLine}{await ContainerLogTailAsync()}",
                        exception);
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    /// <summary>
    /// True for the answers a starting store gives and never for a real fault. A 4xx (bad credential, missing
    /// bucket, wrong signature) is a real fault and is not retried; a 5xx or 408 is the gateway's own
    /// "S3 internal error"; and a status of 0 is how the SDK reports a transport failure (no HTTP response at
    /// all: connection refused or closed while the gateway's port is already mapped). A bare
    /// <see cref="TaskCanceledException"/> is a cancelled run, not a slow store, so only the HTTP-client
    /// timeout shape (its inner exception is a <see cref="TimeoutException"/>) is retried.
    /// </summary>
    private static bool IsTransientStartupFailure(Exception exception) =>
        exception switch
        {
            AmazonServiceException service => IsTransientStatus(service.StatusCode),
            AmazonClientException or HttpRequestException => true,
            TaskCanceledException { InnerException: TimeoutException } => true,
            _ => false,
        };

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status == HttpStatusCode.OK
        || status == HttpStatusCode.RequestTimeout
        || (int)status >= 500;

    /// <summary>The container's log tail, so a refused start reports the store's own reason.</summary>
    private async Task<string> ContainerLogTailAsync()
    {
        if (_container is null)
        {
            return "(the container was never created)";
        }

        try
        {
            var (stdout, stderr) = await _container.GetLogsAsync(DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow);
            var log = stdout + stderr;
            const int tailLength = 4000;
            return log.Length <= tailLength ? log : log[^tailLength..];
        }
        catch (Exception exception)
        {
            return $"(reading the container log failed: {exception.Message})";
        }
    }

    /// <summary>The bucket every 06.1.2-T2 test uploads into; a reused container already owning it is fine.</summary>
    private async Task CreateBucketAsync()
    {
        using var client = CreateClient();
        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = BucketName });
        }
        catch (AmazonS3Exception exception)
            when (exception.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // Reused container state is fine — it is the same bucket.
        }
    }

    /// <summary>
    /// One probe write and delete. SeaweedFS allocates its first volume lazily, so this call pays that
    /// allocation once, inside the retry loop, instead of letting whichever test uploads first pay it.
    /// </summary>
    private async Task WarmUpAsync()
    {
        await PutAsync(WarmUpKey, [1]);
        await DeleteAsync(WarmUpKey);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>A signed client for the arrange/assert steps that bypass the presigned URL.</summary>
    internal AmazonS3Client CreateClient() =>
        new(
            new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config
            {
                ServiceURL = Endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = Region,
            });

    /// <summary>
    /// Writes <paramref name="body"/> straight to <paramref name="key"/>, bypassing the presigned
    /// URL's signed <c>Content-Length</c>/checksum — exactly what a caller who ignores the signed
    /// headers would send. The provider-neutral <c>x-amz-checksum-sha256</c> header (T18) carries the
    /// checksum of the bytes actually written, so the store records a checksum that is wrong for the
    /// artifact's declared one. <c>DisableDefaultChecksumValidation</c> keeps the SDK from adding a
    /// second (SDK-generated) checksum, so this request sends only the header set here.
    /// </summary>
    internal async Task PutAsync(string key, byte[] body)
    {
        using var client = CreateClient();
        var request = new PutObjectRequest
        {
            BucketName = BucketName,
            Key = key,
            InputStream = new MemoryStream(body),
            DisableDefaultChecksumValidation = true,
        };

        request.Headers["x-amz-checksum-sha256"] = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(body));

        await client.PutObjectAsync(request);
    }

    /// <summary>Deletes one object (simulating the 24 h lifecycle or the staging-delete task).</summary>
    internal async Task DeleteAsync(string key)
    {
        using var client = CreateClient();
        await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = BucketName, Key = key });
    }

    /// <summary>True when the object exists (a HEAD that does not 404).</summary>
    internal async Task<bool> ExistsAsync(string key)
    {
        using var client = CreateClient();
        try
        {
            await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = BucketName, Key = key });
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>The stored bytes, or null when the object is absent.</summary>
    internal async Task<byte[]?> ReadAsync(string key)
    {
        using var client = CreateClient();
        try
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = BucketName, Key = key });
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Reads the access key/secret of the container's first identity from the committed file.</summary>
    private static (string AccessKey, string SecretKey) ReadFirstIdentity(FileInfo configFile)
    {
        configFile.Exists.ShouldBeTrue($"the SeaweedFS identity file must exist at {configFile.FullName} (01.8.1 §7)");

        using var document = JsonDocument.Parse(File.ReadAllText(configFile.FullName));
        var credentials = document.RootElement
            .GetProperty("identities")[0]
            .GetProperty("credentials")[0];

        return (
            credentials.GetProperty("accessKey").GetString()
                ?? throw new InvalidOperationException("the SeaweedFS identity file has no accessKey"),
            credentials.GetProperty("secretKey").GetString()
                ?? throw new InvalidOperationException("the SeaweedFS identity file has no secretKey"));
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
}
