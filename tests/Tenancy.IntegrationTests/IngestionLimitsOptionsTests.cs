// 04.1.1-T3 (04.1.1 §10 T3.2): IngestionLimits is bound from EVIDGATE__INGESTION__* and validated at boot.
// Without a database or a host, the same registration chain the module uses runs here (the RunsOptionsTests
// technique), so an out-of-range value fails with the configuration key named and an environment variable
// overrides the option it documents.
using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;
using System.Linq;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Ingestion.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The ingestion caps: the two gzip-decode caps of 04.1.1-T3 and the manifest/report caps of 04.1.3-T1 — their
/// defaults, their range validation, the section they bind from and the environment variable that overrides them.
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
public sealed class IngestionLimitsOptionsTests
{
    [Fact]
    public void Section_is_the_evidgate_ingestion_group()
    {
        // The environment variable form of the key: EVIDGATE__INGESTION__<PROPERTY>.
        IngestionLimits.SectionName.ShouldBe("EVIDGATE:Ingestion");
    }

    [Fact]
    public void Defaults_Validate()
    {
        var defaults = new IngestionLimits();

        defaults.MaxExpandedBytes.ShouldBe(8_388_608);
        defaults.MaxExpansionRatio.ShouldBe(20);
        Validate(defaults).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1_048_576)]
    [InlineData(8_388_608)]
    [InlineData(67_108_864)]
    public void ExpandedBytes_InsideTheBounds_IsAccepted(int bytes)
    {
        Validate(new IngestionLimits { MaxExpandedBytes = bytes }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_048_575)]
    [InlineData(67_108_865)]
    [InlineData(int.MaxValue)]
    public void ExpandedBytes_OutsideTheBounds_IsRejected(int bytes)
    {
        Validate(new IngestionLimits { MaxExpandedBytes = bytes })
            .ShouldContain(message => message.Contains("EVIDGATE__INGESTION__MAXEXPANDEDBYTES", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(1000)]
    public void ExpansionRatio_InsideTheBounds_IsAccepted(int ratio)
    {
        Validate(new IngestionLimits { MaxExpansionRatio = ratio }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void ExpansionRatio_OutsideTheBounds_IsRejected(int ratio)
    {
        Validate(new IngestionLimits { MaxExpansionRatio = ratio })
            .ShouldContain(message => message.Contains("EVIDGATE__INGESTION__MAXEXPANSIONRATIO", StringComparison.Ordinal));
    }

    [Fact]
    public void Section_binding_overrides_the_defaults()
    {
        var bound = Bind(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Ingestion:MaxExpandedBytes"] = "16777216",
                ["EVIDGATE:Ingestion:MaxExpansionRatio"] = "5",
            })
            .Build());

        bound.MaxExpandedBytes.ShouldBe(16_777_216);
        bound.MaxExpansionRatio.ShouldBe(5);
    }

    [Fact]
    public void Environment_variable_overrides_the_option()
    {
        // The deployed form of the same key: a double underscore is the section separator.
        const string name = "EVIDGATE__INGESTION__MAXEXPANDEDBYTES";
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "33554432");

            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            configuration["EVIDGATE:Ingestion:MaxExpandedBytes"].ShouldBe("33554432");

            Bind(configuration).MaxExpandedBytes.ShouldBe(33_554_432);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public void Out_of_range_value_fails_boot_naming_the_key()
    {
        // The module's registration chain, with one bad value: resolving the option throws the
        // OptionsValidationException the host start-up would, and its message names the environment key.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Ingestion:MaxExpansionRatio"] = "5000",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services
            .AddOptions<IngestionLimits>()
            .BindConfiguration(IngestionLimits.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        using var provider = services.BuildServiceProvider();

        var failure = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<IngestionLimits>>().Value);
        failure.Message.ShouldContain("EVIDGATE__INGESTION__MAXEXPANSIONRATIO");
    }

    [Fact]
    public void Cap_defaults_are_the_published_numbers()
    {
        var defaults = new IngestionLimits();

        defaults.MaxManifestBytes.ShouldBe(32 * 1024 * 1024);
        defaults.MaxManifestExecutions.ShouldBe(150_000);
        defaults.MaxManifestVariants.ShouldBe(20);
        defaults.MaxReportCases.ShouldBe(150_000);
        Validate(defaults).ShouldBeEmpty();
    }

    /// <summary>
    /// The parser's own fallback entry cap is the published report cap, so the number the spec publishes
    /// (<c>x-evidgate-limits.report_cases</c>) is the one the parser applies when nothing is configured.
    /// </summary>
    [Fact]
    public void Parser_default_report_cap_is_the_published_cap()
    {
        ParsingService.DefaultMaxReportCases.ShouldBe(IngestionLimits.DefaultMaxReportCases);
    }

    [Theory]
    [InlineData("MaxManifestBytes", 1)]
    [InlineData("MaxManifestBytes", 33_554_432)]
    [InlineData("MaxManifestExecutions", 1)]
    [InlineData("MaxManifestExecutions", 1_000_000)]
    [InlineData("MaxManifestVariants", 1)]
    [InlineData("MaxManifestVariants", 1000)]
    [InlineData("MaxReportCases", 1)]
    [InlineData("MaxReportCases", 1_000_000)]
    public void Cap_inside_its_bounds_is_accepted(string name, int value)
    {
        Validate(With(name, value)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("MaxManifestBytes", 0)]
    [InlineData("MaxManifestBytes", 67_108_865)]
    [InlineData("MaxManifestExecutions", 0)]
    [InlineData("MaxManifestExecutions", 1_000_001)]
    [InlineData("MaxManifestVariants", 0)]
    [InlineData("MaxManifestVariants", 1001)]
    [InlineData("MaxReportCases", 0)]
    [InlineData("MaxReportCases", 1_000_001)]
    public void Cap_outside_its_bounds_is_rejected_naming_its_key(string name, int value)
    {
        var key = "EVIDGATE__INGESTION__" + name.ToUpperInvariant();

        Validate(With(name, value)).ShouldContain(message => message.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public void Cap_section_binding_and_environment_override()
    {
        const string name = "EVIDGATE__INGESTION__MAXMANIFESTEXECUTIONS";
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "5000");

            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            configuration["EVIDGATE:Ingestion:MaxManifestExecutions"].ShouldBe("5000");

            var bound = Bind(configuration);
            bound.MaxManifestExecutions.ShouldBe(5_000);
            bound.MaxManifestBytes.ShouldBe(32 * 1024 * 1024, "an unset cap keeps its published default");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    /// <summary>One option set by name, so the bound tests do not repeat four setters.</summary>
    private static IngestionLimits With(string name, int value) => name switch
    {
        "MaxManifestBytes" => new IngestionLimits { MaxManifestBytes = value },
        "MaxManifestExecutions" => new IngestionLimits { MaxManifestExecutions = value },
        "MaxManifestVariants" => new IngestionLimits { MaxManifestVariants = value },
        "MaxReportCases" => new IngestionLimits { MaxReportCases = value },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown cap"),
    };

    /// <summary>Binds the section exactly as the module registers it.</summary>
    private static IngestionLimits Bind(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddOptions<IngestionLimits>().BindConfiguration(IngestionLimits.SectionName);

        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<IngestionLimits>>().Value;
    }

    /// <summary>The failure messages <see cref="Validator.TryValidateObject"/> produces, as the host sees them.</summary>
    private static List<string> Validate(IngestionLimits options)
    {
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        return results.Select(result => result.ErrorMessage ?? string.Empty).ToList();
    }
}
