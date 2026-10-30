using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// A queue's concurrency bound must fail the boot, not the claim loop: the list item
/// never reaches <c>ValidateDataAnnotations</c>, and a zero that passed start-up used to
/// stop the consumer inside the hosted service instead of refusing the configuration.
/// </summary>
public sealed class TasksOptionsValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    public void Queue_Concurrency_Outside_The_Range_Refuses_The_Boot(int concurrency)
    {
        var failure = Should.Throw<OptionsValidationException>(() => ValidateOnStart(concurrency));

        failure.Message.ShouldContain("EVIDGATE__TASKS__QUEUES__*__CONCURRENCY");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public void Queue_Concurrency_Inside_The_Range_Passes_The_Boot(int concurrency)
    {
        Should.NotThrow(() => ValidateOnStart(concurrency));
    }

    /// <summary>
    /// The registration and the start-up validation the Worker composes, over an
    /// in-memory section: no host, no database and no consumer is started.
    /// </summary>
    private static void ValidateOnStart(int concurrency)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{TasksOptions.SectionName}:Queues:0:Name"] = "default",
                [$"{TasksOptions.SectionName}:Queues:0:Concurrency"] = concurrency.ToString(CultureInfo.InvariantCulture),
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddEvidgateTasks(configuration)
            .BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
    }
}
