// The two cancel windows as startup validation sees them: both required, strictly positive, ordered, and bounded.
using Evidgate.Modules.Orchestrator;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed class RunCancelOptionsTests
{
    [Theory]
    [InlineData(null, "00:02:00", "SOFTSTOPDELAY")]
    [InlineData("00:00:30", null, "KILLDELAY")]
    [InlineData("00:00:00", "00:02:00", "SOFTSTOPDELAY")]
    [InlineData("-00:00:01", "00:02:00", "SOFTSTOPDELAY")]
    [InlineData("00:00:30", "00:00:00", "KILLDELAY")]
    [InlineData("00:00:30", "-00:00:01", "KILLDELAY")]
    [InlineData("00:02:00", "00:00:30", "KILLDELAY")]
    [InlineData("367.00:00:00", "367.00:00:00", "SOFTSTOPDELAY")]
    [InlineData("00:00:30", "367.00:00:00", "KILLDELAY")]
    public void RunCancelOptions_InvalidDuration_FailsStartupValidation(string? soft, string? kill, string key)
    {
        using var invalid = Provider(soft, kill);
        var exception = Should.Throw<OptionsValidationException>(
            () => invalid.GetRequiredService<IStartupValidator>().Validate());
        exception.Message.ShouldContain($"EVIDGATE__RUNS__CANCEL__{key}");
        using var control = Provider("00:00:30", "00:02:00");
        Should.NotThrow(() => control.GetRequiredService<IStartupValidator>().Validate());
    }

    [Theory]
    [InlineData("00:00:00.0000001", "00:00:00.0000001")]
    [InlineData("366.00:00:00", "366.00:00:00")]
    public void RunCancelOptions_ValidInclusiveBounds_Starts(string soft, string kill)
    {
        using var provider = Provider(soft, kill);
        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
        provider.GetRequiredService<IOptions<RunCancelOptions>>().Value.KillDelay.ShouldBe(TimeSpan.Parse(kill,
            System.Globalization.CultureInfo.InvariantCulture));
    }

    private static ServiceProvider Provider(string? soft, string? kill)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (soft is not null)
        {
            values["EVIDGATE:Runs:Cancel:SoftStopDelay"] = soft;
        }

        if (kill is not null)
        {
            values["EVIDGATE:Runs:Cancel:KillDelay"] = kill;
        }

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddOrchestratorModule();
        return services.BuildServiceProvider();
    }
}
