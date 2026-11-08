using Evidgate.BuildingBlocks.Contracts.Validation;
using Xunit;

// Registered at file scope like DatabaseFixture, so a project that links this file warms the
// schema cache once per test process without touching its csproj fixtures or test classes.
[assembly: AssemblyFixture(typeof(Evidgate.Testing.ContractSchemaWarmup))]

namespace Evidgate.Testing;

/// <summary>
/// Compiles the contract schema set once, before the first test of the assembly runs.
/// <para>
/// <c>ContractSchemaSet.Load</c> compiles every schema with Roslyn: about 55 s on an idle
/// machine for the first load of a process, a few milliseconds for every later load because
/// the validator caches the compiled types process-wide. Every in-process API host loads the
/// set lazily inside the first request that validates a body, so without this warm-up the
/// first requests of a run sit inside the 100 s <c>HttpClient</c> timeout (and inside an open
/// database transaction) while the compiler competes with every parallel test for the CPU,
/// and the ones that lose the race time out.
/// </para>
/// </summary>
public sealed class ContractSchemaWarmup : IAsyncLifetime
{
    /// <inheritdoc />
    public ValueTask InitializeAsync() => new(Task.Run(() => ContractSchemaSet.Load(ContractsDirectory())));

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string ContractsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
            {
                return Path.Combine(directory.FullName, "contracts");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Evidgate.slnx not found above '{AppContext.BaseDirectory}'");
    }
}
