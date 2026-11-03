// The events route's burst budget options: bound from EVIDGATE__INGESTION__RATELIMIT__* and validated at
// boot with the same registration chain the module uses, so an out-of-range value fails before the host serves.
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Evidgate.Modules.Ingestion.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <see cref="IngestionRateLimits"/>: its defaults, its range validation, the section it binds from and the
/// environment variables that override it.
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
public sealed class IngestionRateLimitsOptionsTests
{
    [Fact]
    public void Section_is_the_evidgate_ingestion_ratelimit_group()
    {
        // The environment variable form of the key: EVIDGATE__INGESTION__RATELIMIT__<PROPERTY>.
        IngestionRateLimits.SectionName.ShouldBe("EVIDGATE:Ingestion:RateLimit");
    }

    [Fact]
    public void Defaults_Validate()
    {
        var defaults = new IngestionRateLimits();

        defaults.PermitLimit.ShouldBe(120);
        defaults.WindowSeconds.ShouldBe(60);
        defaults.MaxPartitions.ShouldBe(10_000);
        Validate(defaults).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(120)]
    [InlineData(100_000)]
    public void PermitLimit_InsideTheBounds_IsAccepted(int limit)
    {
        Validate(new IngestionRateLimits { PermitLimit = limit }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_001)]
    public void PermitLimit_OutsideTheBounds_IsRejected(int limit)
    {
        Validate(new IngestionRateLimits { PermitLimit = limit })
            .ShouldContain(message => message.Contains("EVIDGATE__INGESTION__RATELIMIT__PERMITLIMIT", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(3600)]
    public void WindowSeconds_InsideTheBounds_IsAccepted(int seconds)
    {
        Validate(new IngestionRateLimits { WindowSeconds = seconds }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    public void WindowSeconds_OutsideTheBounds_IsRejected(int seconds)
    {
        Validate(new IngestionRateLimits { WindowSeconds = seconds })
            .ShouldContain(message => message.Contains("EVIDGATE__INGESTION__RATELIMIT__WINDOWSECONDS", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void MaxPartitions_InsideTheBounds_IsAccepted(int partitions)
    {
        Validate(new IngestionRateLimits { MaxPartitions = partitions }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_001)]
    public void MaxPartitions_OutsideTheBounds_IsRejected(int partitions)
    {
        Validate(new IngestionRateLimits { MaxPartitions = partitions })
            .ShouldContain(message => message.Contains("EVIDGATE__INGESTION__RATELIMIT__MAXPARTITIONS", StringComparison.Ordinal));
    }

    [Fact]
    public void Section_binding_overrides_the_defaults()
    {
        var bound = Bind(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Ingestion:RateLimit:PermitLimit"] = "5",
                ["EVIDGATE:Ingestion:RateLimit:WindowSeconds"] = "30",
                ["EVIDGATE:Ingestion:RateLimit:MaxPartitions"] = "500",
            })
            .Build());

        bound.PermitLimit.ShouldBe(5);
        bound.WindowSeconds.ShouldBe(30);
        bound.MaxPartitions.ShouldBe(500);
    }

    [Fact]
    public void Environment_variable_overrides_the_option()
    {
        const string name = "EVIDGATE__INGESTION__RATELIMIT__PERMITLIMIT";
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "7");

            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            configuration["EVIDGATE:Ingestion:RateLimit:PermitLimit"].ShouldBe("7");

            Bind(configuration).PermitLimit.ShouldBe(7);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public void Out_of_range_value_fails_boot_naming_the_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Ingestion:RateLimit:WindowSeconds"] = "0",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services
            .AddOptions<IngestionRateLimits>()
            .BindConfiguration(IngestionRateLimits.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        using var provider = services.BuildServiceProvider();

        var failure = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<IngestionRateLimits>>().Value);
        failure.Message.ShouldContain("EVIDGATE__INGESTION__RATELIMIT__WINDOWSECONDS");
    }

    /// <summary>Binds the section exactly as the module registers it.</summary>
    private static IngestionRateLimits Bind(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddOptions<IngestionRateLimits>().BindConfiguration(IngestionRateLimits.SectionName);

        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<IngestionRateLimits>>().Value;
    }

    /// <summary>The failure messages <see cref="Validator.TryValidateObject"/> produces, as the host sees them.</summary>
    private static List<string> Validate(IngestionRateLimits options)
    {
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        return results.Select(result => result.ErrorMessage ?? string.Empty).ToList();
    }
}
