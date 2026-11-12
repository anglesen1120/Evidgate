// The real SeaweedFS S3 gateway the end-to-end `complete` tests PUT bytes to and inspect afterwards.
// One digest-pinned container per test assembly (an xUnit assembly fixture) and one bucket per test class
// on it, emptied and dropped when that class fixture releases, so the class-level seam (endpoint,
// credentials, Configuration()) is unchanged while the suite no longer starts a container per class.
// The fixture itself starts nothing: the first Docker-category SeaweedFsStore starts the one container
// through a single shared start task, so a run that selects only non-Docker tests (the clean-build
// smoke) never opens a Docker socket.
// Only the container-start logic of tests/ObjectStorage.IntegrationTests' SeaweedFsFixture is copied (the
// digest-pinned image, the mounted local-only identity file, the S3 gateway command and the HTTP
// readiness probe).
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

[assembly: AssemblyFixture(typeof(Tenancy.IntegrationTests.SeaweedFsServer))]

namespace Tenancy.IntegrationTests;

/// <summary>
/// The one digest-pinned SeaweedFS S3 gateway of this assembly. The credentials are read from the same
/// committed local-only identity file the dev stack mounts (never a literal); <see cref="Current"/> is the
/// handle each <see cref="SeaweedFsStore"/> borrows its endpoint and credentials from.
/// </summary>
public sealed class SeaweedFsServer : IAsyncLifetime
{
    internal const int S3Port = 8333;

    private const string ContainerConfigPath = "/etc/seaweedfs/s3-config.json";

    private readonly FileInfo _configFile = new(
        Path.Combine(RepositoryRoot(), "infra", "seaweedfs", "s3-config.json"));

    private readonly object _startLock = new();

    private IContainer? _container;

    private Task? _start;

    /// <summary>The assembly fixture instance; its gateway starts with the first store that needs one.</summary>
    internal static SeaweedFsServer? Current { get; private set; }

    /// <summary>The container's S3 endpoint, e.g. <c>http://127.0.0.1:49153</c>.</summary>
    internal string Endpoint { get; private set; } = null!;

    internal string AccessKey { get; private set; } = null!;

    internal string SecretKey { get; private set; } = null!;

    /// <summary>
    /// Starts nothing: xUnit runs this fixture even when the category filter selects no Docker test, so
    /// the first <see cref="SeaweedFsStore"/> starts the shared gateway through <see cref="EnsureStartedAsync"/>.
    /// </summary>
    public ValueTask InitializeAsync()
    {
        Current = this;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The one start task: every store that needs the gateway awaits the same task, so concurrent first
    /// classes start one container and a failed start reaches each of them as that same failure.
    /// </summary>
    internal Task EnsureStartedAsync()
    {
        lock (_startLock)
        {
            return _start ??= Task.Run(StartAsync);
        }
    }

    private async Task StartAsync()
    {
        var identity = ReadFirstIdentity(_configFile);

        _container = await ContainerStart.StartAsync(
            () => new ContainerBuilder(ContainerImages.SeaweedFs)
                .WithResourceMapping(_configFile, new FileInfo(ContainerConfigPath))
                .WithCommand("server", "-s3", $"-s3.port={S3Port}", $"-s3.config={ContainerConfigPath}")
                .WithPortBinding(S3Port, assignRandomHostPort: true)
                .WithWaitStrategy(
                    Wait.ForUnixContainer()
                        .UntilHttpRequestIsSucceeded(
                            request => request.ForPort(S3Port).ForPath("/").ForStatusCodeMatching(_ => true)))
                .Build());

        Endpoint = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(S3Port)}";
        AccessKey = identity.AccessKey;
        SecretKey = identity.SecretKey;
    }

    public async ValueTask DisposeAsync()
    {
        Current = null;
        if (_container is not null)
        {
            // Only a started container is kept here; the shared start helper disposes failed attempts.
            await ContainerStart.DisposeAsync(_container);
        }
    }

    /// <summary>The container's log tail, so a refused start reports the store's own reason.</summary>
    internal async Task<string> ContainerLogTailAsync()
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

/// <summary>
/// A test class's own bucket on the assembly's one gateway (a class fixture, so one bucket per class). It
/// keeps the seam the hosts bind (<see cref="Configuration"/>) and the arrange/assert helpers; the unique
/// bucket name is what isolates the class, since the gateway itself is shared.
/// </summary>
public sealed class SeaweedFsStore : IAsyncLifetime
{
    private const string Region = "us-east-1";

    // The start-up retry budget lives in TransientS3RetryOptions.Startup (shared with the parity fixture).

    /// <summary>The probe object of the warm-up (written and deleted inside the retry loop).</summary>
    private const string WarmUpKey = "warm-up/probe";

    /// <summary>Base of the per-class bucket name; the instance appends a random suffix so classes never share one.</summary>
    private const string BucketPrefix = "evidgate-evidence-complete";

    /// <summary>
    /// The whole 32-hex suffix matters: a v7 GUID's leading hex digits are timestamp bits, so a truncated
    /// one repeats for every class started inside the same time window and two classes would share a bucket.
    /// </summary>
    private readonly string _bucketName = $"{BucketPrefix}-{Guid.NewGuid().ToString("N")}";

    /// <summary>This class's bucket on the shared gateway.</summary>
    internal string BucketName => _bucketName;

    /// <summary>The assembly fixture instance; it starts the gateway on this class's first use.</summary>
    private static SeaweedFsServer Server => SeaweedFsServer.Current
        ?? throw new InvalidOperationException(
            "the SeaweedFS assembly fixture is not active; xUnit starts it before any class fixture");

    /// <summary>The shared container's S3 endpoint, e.g. <c>http://127.0.0.1:49153</c>.</summary>
    internal string Endpoint { get; private set; } = null!;

    internal string AccessKey { get; private set; } = null!;

    internal string SecretKey { get; private set; } = null!;

    /// <summary>
    /// The <c>EVIDGATE:Object</c> configuration the API host binds — the same keys the 06.1.2-T1
    /// suite uses, with the endpoint, the credentials of the shared container and this class's bucket.
    /// Rebuilt per call so a host always sees the running container's mapped port.
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
        var server = Server;
        await server.EnsureStartedAsync();

        Endpoint = server.Endpoint;
        AccessKey = server.AccessKey;
        SecretKey = server.SecretKey;

        // Both first S3 calls of a class run under this retry, and until F59 neither was protected:
        // the bucket creation had no retry and the warm-up that followed retried only AmazonServiceException.
        // A starting gateway is not writable yet — in the container-log capture taken while its mapped port
        // already answered, the master was still electing itself ("raft.Server: Not current leader") and S3
        // calls were refused with a reset connection. That window is the suspected cause of the F59 500
        // "S3 internal error" that failed all 12 cases of the class at fixture init (the 500 itself was not
        // reproduced locally). Retrying both calls, transiently only, keeps every later write ordinary.
        await RetryWhileStartingAsync(async cancellationToken =>
        {
            await CreateBucketAsync(cancellationToken);
            await WarmUpAsync(cancellationToken);
        });
    }

    /// <summary>
    /// The shared bounded, transient-only retry, with the container's log tail attached when the budget is
    /// spent so the next flake carries its own cause instead of a bare 500.
    /// </summary>
    private static async Task RetryWhileStartingAsync(Func<CancellationToken, Task> step) =>
        await TransientS3Retry.RunAsync(
            step,
            TransientS3RetryOptions.Startup,
            async () => $"Container log tail:{Environment.NewLine}{await Server.ContainerLogTailAsync()}");

    /// <summary>The bucket this class uploads into; a retry after a partial attempt may find it already created.</summary>
    private async Task CreateBucketAsync(CancellationToken cancellationToken)
    {
        using var client = CreateStartupClient();
        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = BucketName }, cancellationToken);
        }
        catch (AmazonS3Exception exception)
            when (exception.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // A retried attempt on the same class keeps the bucket it created.
        }
    }

    /// <summary>
    /// One probe write and delete. SeaweedFS allocates its first volume lazily, so this call pays that
    /// allocation once, inside the retry loop, instead of letting whichever test uploads first pay it.
    /// </summary>
    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        using var client = CreateStartupClient();
        await PutAsync(client, WarmUpKey, [1], cancellationToken);
        await DeleteAsync(client, WarmUpKey, cancellationToken);
    }

    /// <summary>
    /// Empties this class's bucket and drops it, so the shared gateway does not accumulate one bucket per
    /// test class. Teardown is best effort on purpose: a bucket left behind only occupies space on a
    /// container that stops with the assembly, and failing here would mask the run's real result.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            using var client = CreateClient();
            await DeleteAllObjectsAsync(client, CancellationToken.None);
            await client.DeleteBucketAsync(
                new DeleteBucketRequest { BucketName = BucketName }, CancellationToken.None);
        }
        catch (Exception)
        {
            // Covers the absent bucket of a class whose init failed, a gateway already gone, and any
            // delete answer: none of them may fail a run that has already produced its results.
        }
    }

    /// <summary>Deletes every object the class left in its bucket; an absent bucket lists as empty.</summary>
    private async Task DeleteAllObjectsAsync(AmazonS3Client client, CancellationToken cancellationToken)
    {
        string? continuationToken = null;
        do
        {
            var page = await client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = BucketName,
                    ContinuationToken = continuationToken,
                },
                cancellationToken);

            foreach (var item in page.S3Objects ?? [])
            {
                await DeleteAsync(client, item.Key, cancellationToken);
            }

            continuationToken = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (continuationToken is not null);
    }

    /// <summary>A signed client for the arrange/assert steps that bypass the presigned URL.</summary>
    internal AmazonS3Client CreateClient() => new(new BasicAWSCredentials(AccessKey, SecretKey), ClientConfig());

    private AmazonS3Client CreateStartupClient() =>
        TransientS3Retry.CreateClient(Endpoint, AccessKey, SecretKey);

    private AmazonS3Config ClientConfig() => new()
    {
        ServiceURL = Endpoint,
        ForcePathStyle = true,
        AuthenticationRegion = Region,
    };

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
        await PutAsync(client, key, body, CancellationToken.None);
    }

    private async Task PutAsync(
        AmazonS3Client client, string key, byte[] body, CancellationToken cancellationToken)
    {
        var request = new PutObjectRequest
        {
            BucketName = BucketName,
            Key = key,
            InputStream = new MemoryStream(body),
            DisableDefaultChecksumValidation = true,
        };

        request.Headers["x-amz-checksum-sha256"] = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(body));

        await client.PutObjectAsync(request, cancellationToken);
    }

    /// <summary>Deletes one object (simulating the 24 h lifecycle or the staging-delete task).</summary>
    internal async Task DeleteAsync(string key)
    {
        using var client = CreateClient();
        await DeleteAsync(client, key, CancellationToken.None);
    }

    private async Task DeleteAsync(AmazonS3Client client, string key, CancellationToken cancellationToken) =>
        await client.DeleteObjectAsync(
            new DeleteObjectRequest { BucketName = BucketName, Key = key }, cancellationToken);

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
}
