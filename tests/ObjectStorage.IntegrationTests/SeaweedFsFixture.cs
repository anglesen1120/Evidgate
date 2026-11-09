using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace ObjectStorage.IntegrationTests;

/// <summary>
/// SeaweedFS test fixture for the 01.8.1-T1 parity suite: starts the digest-pinned
/// container image from <see cref="ContainerImages.SeaweedFs"/> with the S3
/// gateway enabled and the local-only identity file from <c>infra/seaweedfs/</c>
/// mounted at the same path the dev compose stack will use. Readiness is the S3
/// port answering HTTP — no sleeps anywhere in this suite.
/// </summary>
public sealed class SeaweedFsFixture : IAsyncLifetime
{
    public const int S3Port = 8333;

    private const string ContainerConfigPath = "/etc/seaweedfs/s3-config.json";
    private const string Region = "us-east-1";
    private const string BucketName = "evidgate-parity";

    private readonly FileInfo _configFile = new(
        Path.Combine(RepositoryRoot(), "infra", "seaweedfs", "s3-config.json"));

    private readonly FileInfo _lifecycleFile = new(
        Path.Combine(RepositoryRoot(), "infra", "seaweedfs", "pending-lifecycle.xml"));

    private IContainer? _container;

    /// <summary>The endpoint-agnostic target the assertion set runs against this container.</summary>
    internal S3ParityTarget Target { get; private set; } = null!;

    /// <summary>
    /// The <c>pending/</c> 24 h lifecycle rule the fixture put on the bucket, read
    /// from the committed XML the dev stack uploads (AC7).
    /// </summary>
    internal PendingLifecycleRule PendingLifecycle { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var identity = ReadFirstIdentity(_configFile);

        _container = new ContainerBuilder(ContainerImages.SeaweedFs)
            .WithResourceMapping(_configFile, new FileInfo(ContainerConfigPath))
            .WithCommand(
                "server",
                "-s3",
                $"-s3.port={S3Port}",
                $"-s3.config={ContainerConfigPath}",
                // Each bucket that receives an object takes its own volume; the default slot count
                // runs out after a handful of buckets and answers InternalError on the next write.
                "-volume.max=64")
            .WithPortBinding(S3Port, assignRandomHostPort: true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(
                        request => request
                            .ForPort(S3Port)
                            .ForPath("/")
                            .ForStatusCodeMatching(_ => true)))
            .Build();

        await _container.StartAsync();

        Target = new S3ParityTarget
        {
            ServiceUrl = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(S3Port)}",
            Region = Region,
            AccessKey = identity.AccessKey,
            SecretKey = identity.SecretKey,
            Bucket = BucketName,
        };

        await CreateBucketAsync();
        await ApplyPendingLifecycleAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>The bucket the parity keys live in, created with the same credentials the tests use.</summary>
    private async Task CreateBucketAsync()
    {
        await RetryWhileStartingAsync(async cancellationToken =>
        {
            using var client = CreateStartupClient();
            try
            {
                await client.PutBucketAsync(new PutBucketRequest { BucketName = Target.Bucket }, cancellationToken);
            }
            catch (AmazonS3Exception exception)
                when (exception.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
            {
                // Reused container state is fine — the bucket is the same.
            }
        });
    }

    /// <summary>
    /// A freshly started gateway refuses its first writes for a moment (master election, lazy first
    /// volume), so both set-up calls retry through the shared transient-only helper; a 4xx still fails at once.
    /// </summary>
    private async Task RetryWhileStartingAsync(Func<CancellationToken, Task> step) =>
        await TransientS3Retry.RunAsync(
            step,
            TransientS3RetryOptions.Startup,
            ContainerLogTailAsync);

    private AmazonS3Client CreateStartupClient() =>
        TransientS3Retry.CreateClient(Target.ServiceUrl, Target.AccessKey, Target.SecretKey);

    private async Task<string> ContainerLogTailAsync()
    {
        if (_container is null)
        {
            return "(no container)";
        }

        try
        {
            var (stdout, stderr) = await _container.GetLogsAsync(DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow);
            var log = stdout + stderr;
            const int tailLength = 4000;
            return $"Container log tail:{Environment.NewLine}"
                + (log.Length <= tailLength ? log : log[^tailLength..]);
        }
        catch (Exception exception)
        {
            return $"(reading the container log failed: {exception.Message})";
        }
    }

    /// <summary>
    /// Puts the committed <c>pending/</c> 24 h lifecycle rule on the bucket — the
    /// same rule <c>scripts/dev/up.sh</c> applies to the dev bucket (AC7). The rule
    /// is parsed from <c>infra/seaweedfs/pending-lifecycle.xml</c>, the file the dev
    /// stack uploads verbatim, so the fixture and the dev stack cannot drift apart.
    /// </summary>
    private async Task ApplyPendingLifecycleAsync()
    {
        PendingLifecycle = ReadPendingLifecycle(_lifecycleFile);

        var request = new PutLifecycleConfigurationRequest
        {
            BucketName = Target.Bucket,
            Configuration = new LifecycleConfiguration
            {
                Rules =
                [
                    new LifecycleRule
                    {
                        Id = PendingLifecycle.Id,
                        Filter = new LifecycleFilter
                        {
                            LifecycleFilterPredicate = new LifecyclePrefixPredicate
                            {
                                Prefix = PendingLifecycle.Prefix,
                            },
                        },
                        Status = LifecycleRuleStatus.Enabled,
                        Expiration = new LifecycleRuleExpiration { Days = PendingLifecycle.ExpirationDays },
                    },
                ],
            },
        };

        await RetryWhileStartingAsync(async cancellationToken =>
        {
            using var client = CreateStartupClient();
            await client.PutLifecycleConfigurationAsync(request, cancellationToken);
        });
    }

    /// <summary>Reads the rule out of the committed lifecycle XML (the dev stack's file).</summary>
    private static PendingLifecycleRule ReadPendingLifecycle(FileInfo lifecycleFile)
    {
        var rule = XDocument.Load(lifecycleFile.FullName).Root?.Element("Rule")
            ?? throw new InvalidOperationException($"'{lifecycleFile.Name}' has no Rule element");

        return new PendingLifecycleRule(
            Required(rule, "ID"),
            Required(rule.Element("Filter"), "Prefix"),
            int.Parse(Required(rule.Element("Expiration"), "Days"), CultureInfo.InvariantCulture));
    }

    private static string Required(XElement? parent, string name) =>
        parent?.Element(name)?.Value
        ?? throw new InvalidOperationException($"the lifecycle rule has no '{name}' element");

    /// <summary>The <c>pending/</c> lifecycle rule the fixture applied, read from the committed XML (AC7).</summary>
    internal sealed record PendingLifecycleRule(string Id, string Prefix, int ExpirationDays);

    /// <summary>
    /// Reads the access key/secret the container's S3 gateway will accept, so the
    /// fixture and the container can never drift apart. Only the first identity
    /// (the local-only test one) is used.
    /// </summary>
    private static S3Identity ReadFirstIdentity(FileInfo configFile)
    {
        configFile.Exists.ShouldBeTrue(
            $"the SeaweedFS identity file must exist at {configFile.FullName} (01.8.1 §7)");

        using var document = JsonDocument.Parse(File.ReadAllText(configFile.FullName));
        var credentials = document.RootElement
            .GetProperty("identities")[0]
            .GetProperty("credentials")[0];
        return new S3Identity(
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

    private sealed record S3Identity(string AccessKey, string SecretKey);
}

/// <summary>One SeaweedFS container shared by every parity test (started once, no per-test sleeps).</summary>
[CollectionDefinition(Name)]
public sealed class SeaweedFsCollectionDefinition : ICollectionFixture<SeaweedFsFixture>
{
    public const string Name = "SeaweedFS";
}
