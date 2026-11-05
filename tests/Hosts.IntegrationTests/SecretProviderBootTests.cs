using Evidgate.Api;
using Evidgate.BuildingBlocks.Persistence.Options;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Identity.Options;
using Evidgate.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// 10.1.1-T2.2 (AC): changing an environment-variable value with no code change
/// changes the resolved secret at the next process start. Two Api hosts (and two
/// Worker hosts) are built with different injected values for the database
/// connection string, the CI pepper and the sweep connection string; the resolved
/// options and <see cref="ISecretProvider"/> must follow the injection.
/// </summary>
/// <remarks>
/// No database is needed — the hosts are built, not started — so this is not a
/// Docker test. Values are compared through equality booleans with value-free
/// messages: a failing assertion must never print a secret.
/// </remarks>
public sealed class SecretProviderBootTests
{
    private const string FirstConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=first;Database=evidgate;Timeout=2";

    private const string SecondConnectionString =
        "Host=127.0.0.1;Port=2;Username=evidgate_app;Password=second;Database=evidgate;Timeout=2";

    private const string FirstSweepConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_worker_sched;Password=first;Database=evidgate;Timeout=2";

    private const string SecondSweepConnectionString =
        "Host=127.0.0.1;Port=2;Username=evidgate_worker_sched;Password=second;Database=evidgate;Timeout=2";

    private static readonly string FirstPepper = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
    private static readonly string SecondPepper = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray());

    [Fact]
    public async Task Api_Resolved_Secrets_Follow_The_Injected_Environment()
    {
        var first = await ResolveApiAsync(FirstConnectionString, FirstPepper);
        var second = await ResolveApiAsync(SecondConnectionString, SecondPepper);

        (first.ConnectionString == FirstConnectionString)
            .ShouldBeTrue("the first Api host resolves the first injected connection string");
        (second.ConnectionString == SecondConnectionString)
            .ShouldBeTrue("the second Api host resolves the second injected connection string");
        (first.ConnectionString == second.ConnectionString)
            .ShouldBeFalse("the resolved connection string must change with the injected value");

        (first.Pepper == FirstPepper).ShouldBeTrue("the first Api host resolves the first injected pepper");
        (second.Pepper == SecondPepper).ShouldBeTrue("the second Api host resolves the second injected pepper");
        (first.Pepper == second.Pepper)
            .ShouldBeFalse("the resolved pepper must change with the injected value");
    }

    [Fact]
    public async Task Api_Provider_Resolves_The_Same_Value_As_The_Options()
    {
        var resolved = await ResolveApiAsync(FirstConnectionString, FirstPepper);

        (resolved.ProviderConnectionString == FirstConnectionString)
            .ShouldBeTrue("ISecretProvider resolves the injected connection string");
    }

    [Fact]
    public void Worker_Resolved_Sweep_Connection_String_Follows_The_Injected_Environment()
    {
        var first = ResolveWorkerSweep(FirstSweepConnectionString);
        var second = ResolveWorkerSweep(SecondSweepConnectionString);

        (first == FirstSweepConnectionString)
            .ShouldBeTrue("the first Worker host resolves the first injected sweep connection string");
        (second == SecondSweepConnectionString)
            .ShouldBeTrue("the second Worker host resolves the second injected sweep connection string");
        (first == second)
            .ShouldBeFalse("the resolved sweep connection string must change with the injected value");
    }

    private static async Task<ApiSecrets> ResolveApiAsync(string connectionString, string pepper)
    {
        var builder = ApiHost.CreateBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:Database:ConnectionString"] = connectionString,
            ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30",
            ["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00",
            ["EVIDGATE:Ci:Pepper"] = pepper,
            [IsolationSelfCheckEnvironment.InMemoryKey] = IsolationSelfCheckEnvironment.Disabled,
        });

        var app = builder.Build();
        try
        {
            var provider = app.Services.GetRequiredService<ISecretProvider>();
            var database = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var ci = app.Services.GetRequiredService<IOptions<CiAuthOptions>>().Value;
            var providerValue = await provider.GetSecretAsync(SecretNames.DatabaseConnectionString);

            return new ApiSecrets(database.ConnectionString, ci.Pepper, providerValue);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static string? ResolveWorkerSweep(string sweepConnectionString)
    {
        var builder = WorkerHost.CreateBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:Database:ConnectionString"] = FirstConnectionString,
            ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30",
            ["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00",
            ["EVIDGATE:Tasks:SweepConnectionString"] = sweepConnectionString,
            [IsolationSelfCheckEnvironment.InMemoryKey] = IsolationSelfCheckEnvironment.Disabled,
        });

        using var host = builder.Build();

        return host.Services.GetRequiredService<IOptions<TasksOptions>>().Value.SweepConnectionString;
    }

    private sealed record ApiSecrets(string ConnectionString, string? Pepper, string? ProviderConnectionString);
}
