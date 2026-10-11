using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.1-T1 (F4) without a database: <see cref="RunsOptions"/> is validated at boot through the module's
/// <c>ValidateDataAnnotations</c>, which is exactly <see cref="Validator.TryValidateObject"/> with
/// <c>validateAllProperties: true</c>, so the same path runs here. A non-positive or over-long
/// <see cref="RunsOptions.DefaultCollectionTimeout"/> would put the immutable <c>run.collection_deadline_at</c>
/// at or before <c>created_at</c>, or overflow the <c>interval</c> arithmetic that computes it.
/// </summary>
public sealed class RunsOptionsTests
{
    [Fact]
    public void Defaults_Validate()
    {
        Validate(new RunsOptions()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(366)]
    public void CollectionTimeout_InsideTheBounds_IsAccepted(int days)
    {
        Validate(new RunsOptions { DefaultCollectionTimeout = TimeSpan.FromDays(days) }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1_000_000)]
    public void CollectionTimeout_NonPositive_IsRejected(int hours)
    {
        Validate(new RunsOptions { DefaultCollectionTimeout = TimeSpan.FromHours(hours) })
            .ShouldContain(message => message.Contains("DefaultCollectionTimeout", StringComparison.Ordinal));
    }

    [Fact]
    public void CollectionTimeout_OverTheUpperBound_IsRejected()
    {
        Validate(new RunsOptions { DefaultCollectionTimeout = TimeSpan.FromDays(366) + TimeSpan.FromSeconds(1) })
            .ShouldContain(message => message.Contains("DefaultCollectionTimeout", StringComparison.Ordinal));
    }

    [Fact]
    public void CollectionTimeout_MaxValue_IsRejected()
    {
        Validate(new RunsOptions { DefaultCollectionTimeout = TimeSpan.MaxValue })
            .ShouldContain(message => message.Contains("DefaultCollectionTimeout", StringComparison.Ordinal));
    }

    /// <summary>The failure messages <see cref="Validator.TryValidateObject"/> produces, as the host sees them.</summary>
    private static List<string> Validate(RunsOptions options)
    {
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        return results.Select(result => result.ErrorMessage ?? string.Empty).ToList();
    }
}
