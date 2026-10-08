using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// 10.1.1-T2 (QA-SECRET-04): <see cref="MigratorOptions.FromEnvironment"/> takes its
/// connection string from the <see cref="ISecretProvider"/> it is given, so a managed
/// secret store (O3) replaces the environment without touching the Migrator.
/// </summary>
/// <remarks>
/// The connection strings carry no password, and every assertion compares a boolean
/// with a value-free message, so a failure never prints a connection string.
/// </remarks>
public sealed class MigratorSecretSeamTests
{
    private const string EnvironmentConnectionString = "Host=env.invalid;Port=5432;Database=db_env;Username=user_env";

    private const string ProviderConnectionString = "Host=provider.invalid;Port=5432;Database=db_provider;Username=user_provider";

    [Fact]
    public void Replacement_Provider_Wins_Over_The_Environment_Value()
    {
        var provider = new RecordingSecretProvider(ProviderConnectionString);

        var options = MigratorOptions.FromEnvironment(
            EnvironmentWith(EnvironmentConnectionString),
            secrets: provider);

        string.Equals(options.ConnectionString, ProviderConnectionString, StringComparison.Ordinal)
            .ShouldBeTrue("the connection string must come from the replacement provider");
        string.Equals(options.ConnectionString, EnvironmentConnectionString, StringComparison.Ordinal)
            .ShouldBeFalse("the environment value must not be used when a provider is passed");
        provider.RequestedNames.SequenceEqual([SecretNames.MigratorConnectionString], StringComparer.Ordinal)
            .ShouldBeTrue("the provider must be asked for the Migrator connection-string secret only");
    }

    [Fact]
    public void Replacement_Provider_Without_The_Secret_Is_Reported_As_Missing()
    {
        // The environment has a value, but the provider is the only source once passed.
        var error = Should.Throw<MigratorSetupException>(() => MigratorOptions.FromEnvironment(
            EnvironmentWith(EnvironmentConnectionString),
            secrets: new RecordingSecretProvider(value: null)));

        error.Errors.Any(message => message.Contains(MigratorOptions.ConnectionStringVariable, StringComparison.Ordinal))
            .ShouldBeTrue("an unset provider secret must be reported as missing");
    }

    [Fact]
    public void Without_A_Provider_The_Environment_Snapshot_Is_The_Source()
    {
        var options = MigratorOptions.FromEnvironment(EnvironmentWith(EnvironmentConnectionString));

        string.Equals(options.ConnectionString, EnvironmentConnectionString, StringComparison.Ordinal)
            .ShouldBeTrue("the default provider must read the environment snapshot");
    }

    private static Dictionary<string, string?> EnvironmentWith(string connectionString) =>
        new(StringComparer.Ordinal)
        {
            [MigratorOptions.ConnectionStringVariable] = connectionString,
            [MigratorOptions.MigrationsPathVariable] = Path.GetTempPath(),
        };

    private sealed class RecordingSecretProvider(string? value) : ISecretProvider
    {
        public List<string> RequestedNames { get; } = [];

        public ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            RequestedNames.Add(name);

            return ValueTask.FromResult(
                string.Equals(name, SecretNames.MigratorConnectionString, StringComparison.Ordinal) ? value : null);
        }
    }
}
