using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;

namespace Hosts.IntegrationTests;

/// <summary>
/// The environment a real Worker child process needs. Everything here is either a value the host refuses to boot
/// without or one that keeps a test's readiness poll fast — and the worker-role credential is the one that must
/// never be missing: <c>evidgate_worker</c> is the only role with DML on <c>public.task</c> and <c>public.outbox</c>,
/// so the keyed task data source refuses to build for a connection string that does not name it, and the Worker
/// exits 1 with <c>Configuration invalid</c> before it reaches whatever the test is about.
/// </summary>
internal static class WorkerChildEnvironment
{
    /// <summary>
    /// The credential key in the environment-variable provider's spelling
    /// (<c>EVIDGATE__TASKS__WORKERCONNECTIONSTRING</c>). A child process reads the environment, so the in-memory
    /// <c>:</c> spelling a host built inside the test uses does not apply here.
    /// </summary>
    private const string CredentialVariable = SecretNames.TasksWorkerConnectionString;

    /// <summary>
    /// A well-formed worker credential on a closed port, for a Worker that must fail or boot for a reason that is
    /// not the database. It carries no password on purpose: the role check reads the username alone, and a literal
    /// password in a connection string is what the secret scan forbids.
    /// </summary>
    internal const string Unreachable =
        "Host=127.0.0.1;Port=1;Username=evidgate_worker;Database=evidgate;Timeout=2";

    /// <summary>Builds the environment of one Worker child process.</summary>
    /// <param name="databaseConnectionString">The application role the Worker reads the journal and tenant scopes with.</param>
    /// <param name="workerConnectionString">The worker-role credential the queue machinery opens its connections with.</param>
    /// <param name="healthPort">The port the readiness listener binds.</param>
    /// <param name="isolationSelfCheckDisabled">
    /// Whether the boot-time isolation self-check is opted out of. Only a test that boots on a database without the
    /// catalogue, or with a closed port on purpose, says so; the refusal itself is proven against real processes in
    /// <see cref="IsolationSelfCheckBootTests"/>.
    /// </param>
    internal static Dictionary<string, string> Build(
        string databaseConnectionString,
        string workerConnectionString,
        int healthPort,
        bool isolationSelfCheckDisabled = false)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = databaseConnectionString,
            [CredentialVariable] = workerConnectionString,
            ["EVIDGATE__RUNS__CANCEL__SOFTSTOPDELAY"] = "00:00:30",
            ["EVIDGATE__RUNS__CANCEL__KILLDELAY"] = "00:02:00",
            ["EVIDGATE__WORKER__HEALTHPORT"] = healthPort.ToString(CultureInfo.InvariantCulture),
            ["EVIDGATE__WORKER__READINESSPOLLMILLISECONDS"] = "100",
        };

        if (isolationSelfCheckDisabled)
        {
            environment[IsolationSelfCheckEnvironment.Variable] = IsolationSelfCheckEnvironment.Disabled;
        }

        return environment;
    }
}
