using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace ObjectStorage.IntegrationTests;

/// <summary>
/// Options-level cases for the object-storage adapter (06.1.2-T1): the lazy
/// validator's refusals and the secret seam. No container is involved, so these
/// run everywhere — including the Docker-less smoke build.
/// </summary>
public sealed class ObjectStorageOptionsTests
{
    [Theory]
    [InlineData("http://user:pass@127.0.0.1:8333")]
    [InlineData("http://127.0.0.1:8333/?x=1")]
    [InlineData("http://127.0.0.1:8333/#frag")]
    [InlineData("ftp://127.0.0.1:8333")]
    [InlineData("127.0.0.1:8333")]
    public void Endpoint_That_Is_Not_A_Plain_Absolute_Http_Url_Is_Rejected(string endpoint)
    {
        var options = Valid();
        options.Endpoint = endpoint;

        var result = new ObjectStorageOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull().ShouldContain(
            failure => failure.Contains(nameof(ObjectStorageOptions.Endpoint), StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_Bucket_Is_Rejected()
    {
        var options = Valid();
        options.Bucket = null;

        var result = new ObjectStorageOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull().ShouldContain(
            failure => failure.Contains(nameof(ObjectStorageOptions.Bucket), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(901)]
    public void Expiry_Outside_One_To_Nine_Hundred_Seconds_Is_Rejected(int expirySeconds)
    {
        var options = Valid();
        options.PresignExpirySeconds = expirySeconds;

        var result = new ObjectStorageOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull().ShouldContain(
            failure => failure.Contains(nameof(ObjectStorageOptions.PresignExpirySeconds), StringComparison.Ordinal));
    }

    [Fact]
    public void Valid_Options_Pass_Validation()
    {
        new ObjectStorageOptionsValidator().Validate(null, Valid()).Succeeded.ShouldBeTrue();
    }

    /// <summary>
    /// The two credentials have internal setters, so the binder skips them: a value
    /// placed in configuration under the secret's key never reaches the options, and
    /// the post-configurer resolves them through <c>ISecretProvider</c> instead
    /// (10.1.1-T2).
    /// </summary>
    [Fact]
    public void Credentials_Come_From_The_Secret_Provider_And_Not_From_Configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Object:Bucket"] = "evidgate-parity",
                ["EVIDGATE:Object:Region"] = "us-east-1",
                ["EVIDGATE:Object:Endpoint"] = "http://127.0.0.1:8333",
                [SecretNames.ToConfigurationKey(SecretNames.ObjectAccessKey)] = "from-configuration",
                [SecretNames.ToConfigurationKey(SecretNames.ObjectSecretKey)] = "from-configuration",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ISecretProvider>(new EnvironmentSecretProvider(name => name switch
        {
            SecretNames.ObjectAccessKey => "from-provider",
            SecretNames.ObjectSecretKey => "from-provider",
            _ => null,
        }));
        services.AddEvidgateObjectStorage();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;

        options.Bucket.ShouldBe("evidgate-parity");
        options.AccessKey.ShouldBe("from-provider");
        options.SecretKey.ShouldBe("from-provider");
    }

    private static ObjectStorageOptions Valid() => new()
    {
        Endpoint = "http://127.0.0.1:8333",
        Region = "us-east-1",
        Bucket = "evidgate-parity",
        AccessKey = "local",
        SecretKey = "local",
    };
}
