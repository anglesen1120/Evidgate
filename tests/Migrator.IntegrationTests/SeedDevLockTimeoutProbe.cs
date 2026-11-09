using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Npgsql;
using Xunit;

namespace Migrator.IntegrationTests;

[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class SeedDevLockTimeoutProbe
{
    private readonly SeedDevTokenHarness _harness;
    private readonly Xunit.ITestOutputHelper _output;

    public SeedDevLockTimeoutProbe(PostgresMigratorFixture fixture, Xunit.ITestOutputHelper output)
    {
        _harness = new SeedDevTokenHarness(fixture);
        _output = output;
    }

    [Fact]
    public async Task Probe_LockWaitBeyondCommandTimeout_Surfaces()
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();
        var variables = _harness.Variables(database, pepper);
        variables[SecretNames.DatabaseConnectionString] = SeedDevTokenHarness.WithCommandTimeout(
            variables[SecretNames.DatabaseConnectionString]!, 3);

        var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData("evidgate.dev-seed.ingest-token"u8));
        await using var holder = await _harness.Fixture.OpenSuperuserAsync(database);
        await using (var cmd = holder.CreateCommand())
        {
            cmd.CommandText = "select pg_advisory_lock(@k)";
            cmd.Parameters.AddWithValue("k", key);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var clock = Stopwatch.StartNew();
        var run = SeedDevTokenHarness.Run(["seed-dev", "--with-token"], variables);
        _output.WriteLine($"PROBE exit={run.ExitCode} after {clock.Elapsed.TotalSeconds:F1}s (command timeout 3 s) error={run.Error}");
        Assert.Fail($"PROBE exit={run.ExitCode} after {clock.Elapsed.TotalSeconds:F1}s (command timeout 3 s) error={run.Error}");
    }
}
