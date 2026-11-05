using System.Globalization;
using Evidgate.Api;
using Evidgate.BuildingBlocks.Persistence.Options;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>10.1.1-T2.2: real Api process restarts and the managed-provider swap point.</summary>
public sealed class SecretProviderQaTests
{
    [Fact]
    public async Task Api_Two_Process_Starts_Observe_Changed_Environment_And_Mask_Password()
    {
        var first = await RunApiAsync("qa-alpha.invalid", "qa-first-password");
        var second = await RunApiAsync("qa-beta.invalid", "qa-second-password");

        first.ExitCode.ShouldBe(0);
        second.ExitCode.ShouldBe(0);
        first.Stdout.Contains("qa-alpha.invalid", StringComparison.Ordinal).ShouldBeTrue("first process must log its redacted target");
        second.Stdout.Contains("qa-beta.invalid", StringComparison.Ordinal).ShouldBeTrue("second process must log its redacted target");
        first.Stdout.Contains("qa-beta.invalid", StringComparison.Ordinal).ShouldBeFalse();
        second.Stdout.Contains("qa-alpha.invalid", StringComparison.Ordinal).ShouldBeFalse();
        first.Stdout.Contains("Password=***", StringComparison.Ordinal).ShouldBeTrue();
        second.Stdout.Contains("Password=***", StringComparison.Ordinal).ShouldBeTrue();
        var output = first.Stdout + first.Stderr + second.Stdout + second.Stderr;
        output.Contains("qa-first-password", StringComparison.Ordinal).ShouldBeFalse("stdout and stderr must contain no first password");
        output.Contains("qa-second-password", StringComparison.Ordinal).ShouldBeFalse("stdout and stderr must contain no second password");
    }

    [Fact]
    public async Task Registered_Provider_Wins_Over_InMemory_Secret_Configuration()
    {
        var builder = ApiHost.CreateBuilder([]);
        var configured = "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=qa-config;Database=evidgate;Timeout=2";
        var provided = "Host=127.0.0.1;Port=2;Username=evidgate_app;Password=qa-provider;Database=evidgate;Timeout=2";
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EVIDGATE:Database:ConnectionString"] = configured,
            ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30",
            ["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00",
            [IsolationSelfCheckEnvironment.InMemoryKey] = IsolationSelfCheckEnvironment.Disabled,
        });
        builder.Services.AddSingleton<ISecretProvider>(new EnvironmentSecretProvider(
            name => name == SecretNames.DatabaseConnectionString ? provided : null));
        await using var app = builder.Build();
        var actual = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString;
        (actual == provided).ShouldBeTrue("options must resolve through the replacement ISecretProvider, not the binder");
        (actual == configured).ShouldBeFalse("configuration must not bypass the provider");
    }

    private static Task<ProcessRunner.Result> RunApiAsync(string host, string password)
    {
        var port = HostLocator.FreePort();
        var url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        return ProcessRunner.RunThenSigtermAsync(
            HostLocator.AppHost("Evidgate.Api"),
            new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = url,
                [SecretNames.DatabaseConnectionString] = $"Host={host};Port=1;Username=evidgate_app;Password={password};Database=evidgate;Timeout=2",
                [IsolationSelfCheckEnvironment.Variable] = IsolationSelfCheckEnvironment.Disabled,
            },
            url + "/internal/health/live",
            TimeSpan.FromSeconds(60),
            TestContext.Current.CancellationToken);
    }
}
