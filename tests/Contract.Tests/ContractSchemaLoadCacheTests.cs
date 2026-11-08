using System.Diagnostics;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// The integration suites warm the schema set once per process (<c>tests/Shared/ContractSchemaWarmup.cs</c>) and rely on
/// every later <c>ContractSchemaSet.Load</c> of the same directory being served from the validator's process-wide cache.
/// A first load compiles every schema (tens of seconds); if a later load did the same, each in-process API host would
/// stall its first request again, past the 100 s <c>HttpClient</c> timeout under parallel load.
/// </summary>
public sealed class ContractSchemaLoadCacheTests
{
    [Fact]
    public void LoadingTheSameDirectoryAgain_IsServedFromTheProcessCache()
    {
        _ = ContractSchemaSet.Load(ContractPaths.ContractsDirectory);

        var clock = Stopwatch.StartNew();
        var again = ContractSchemaSet.Load(ContractPaths.ContractsDirectory);
        clock.Stop();

        again.Keys.ShouldNotBeEmpty();
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }
}
