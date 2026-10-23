using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Evidgate.BuildingBlocks.Contracts.Validation;

/// <summary>The keys of the contract schemas this repository ships (loaded from <c>contracts/</c>).</summary>
public static class ContractSchemaKeys
{
    /// <summary><c>contracts/schemas/event-envelope.v1.json</c> — 00 §9.4 ingest body.</summary>
    public const string EventEnvelope = "event-envelope.v1";

    /// <summary><c>contracts/adapter/capability.v1.json</c> — 00 §10 adapter protocol document.</summary>
    public const string Capability = "capability.v1";

    /// <summary>
    /// <c>contracts/adapter/operations/execute.v1.json</c> — the execute operation's
    /// result document, which references the 00 §9.4 envelope from
    /// <c>contracts/schemas/event-envelope.v1.json</c> by <c>$id</c> instead of
    /// copying it (04.7.1-T1.2).
    /// </summary>
    public const string AdapterExecute = "execute.v1";

    /// <summary>
    /// <c>contracts/manifest/manifest.v1.json</c> — the <c>POST /v1/projects/{p}/manifests</c>
    /// request body (05.1.1-T1.1), referenced from <c>contracts/openapi.yaml</c>.
    /// </summary>
    public const string Manifest = "manifest.v1";

    /// <summary>
    /// <c>contracts/schemas/environment-revision.v1.json</c> — the <c>POST /v1/projects/{p}/environments/{e}/revisions</c>
    /// request body (05.1.2-T1).
    /// </summary>
    public const string EnvironmentRevision = "environment-revision.v1";

    /// <summary>
    /// <c>contracts/schemas/policy-revision.v1.json</c> — the <c>POST /v1/projects/{p}/policies</c>
    /// request body (07.1.1-T3).
    /// </summary>
    public const string PolicyRevision = "policy-revision.v1";

    /// <summary>
    /// <c>contracts/schemas/authoring-steps.v1.json</c> — the <c>manual_test_case.steps</c> document
    /// of the manual test case create/patch routes (13.1.1-T1.2). Not a request body: it is the
    /// jsonb value carried inside one, so it declares no <c>x-max-bytes</c>.
    /// </summary>
    public const string AuthoringSteps = "authoring-steps.v1";
}

/// <summary>
/// Registration for the contract-validation slice (01.3.2-T2.1): the compiled
/// schema set is a singleton loaded once at startup, and
/// <see cref="ContractValidationEndpointBuilderExtensions.ValidateContractBody{TBuilder}"/>
/// mounts the filter on individual routes — it is deliberately never added
/// globally.
/// </summary>
public static class ContractValidationServiceExtensions
{
    /// <summary>
    /// Loads every <c>contracts/schemas/*.json</c> and <c>contracts/adapter/*.json</c>
    /// schema once at registration and registers the resulting
    /// <see cref="ContractSchemaSet"/> as a singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="contractsDirectory">The repository's <c>contracts/</c> directory.</param>
    public static IServiceCollection AddEvidgateContractValidation(
        this IServiceCollection services,
        string contractsDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractsDirectory);
        return services.AddSingleton(ContractSchemaSet.Load(contractsDirectory));
    }

    /// <summary>
    /// Registers the compiled <see cref="ContractSchemaSet"/> as a singleton that is built <em>on first use</em>
    /// (the host's content root seeds the same directory search as the eager overload), unless a set is already
    /// registered. For modules that read a contract document occasionally (the catalog's sidecar, 03.1.1) and
    /// must not make every host start pay for compiling all schemas.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection TryAddEvidgateContractValidation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(provider =>
        {
            var environment = provider.GetRequiredService<IHostEnvironment>();

            return ContractSchemaSet.Load(LocateContractsDirectory(environment.ContentRootPath, AppContext.BaseDirectory));
        });

        return services;
    }

    /// <summary>
    /// Locates the repository's <c>contracts/</c> directory relative to the
    /// host environment. The content root is <em>not</em> assumed to be the
    /// repository root (it is the project directory under <c>dotnet watch</c>
    /// and the publish directory on a deployed host): the lookup walks upward
    /// from <see cref="IHostEnvironment.ContentRootPath"/> and
    /// <see cref="AppContext.BaseDirectory"/> for a directory containing
    /// <c>contracts/schemas</c> or <c>contracts/adapter</c> and fails loud
    /// naming every searched root when none is found. The
    /// <c>contractsDirectory</c> overload remains the escape hatch for hosts
    /// whose layout carries <c>contracts/</c> somewhere else.
    /// </summary>
    /// <param name="environment">The host environment whose content root seeds the search.</param>
    /// <exception cref="InvalidOperationException">No <c>contracts/</c> directory found above the searched roots.</exception>
    public static IServiceCollection AddEvidgateContractValidation(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        return services.AddEvidgateContractValidation(
            LocateContractsDirectory(environment.ContentRootPath, AppContext.BaseDirectory));
    }

    /// <summary>
    /// Locates the repository's <c>contracts/</c> directory: a candidate is any
    /// ancestor of <paramref name="roots"/> that contains
    /// <c>contracts/schemas</c> or <c>contracts/adapter</c> (the two directories
    /// <see cref="ContractSchemaSet.Load"/> reads). Roots are searched in order
    /// and each is walked to the filesystem root; later roots add candidates
    /// only when the earlier ones missed.
    /// </summary>
    /// <exception cref="InvalidOperationException">No ancestor of any root holds a usable <c>contracts/</c> directory.</exception>
    internal static string LocateContractsDirectory(params ReadOnlySpan<string> roots)
    {
        var candidates = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var directory = new DirectoryInfo(Path.GetFullPath(root));
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "contracts");
                candidates.Add(candidate);
                if (Directory.Exists(Path.Combine(candidate, "schemas"))
                    || Directory.Exists(Path.Combine(candidate, "adapter")))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException(
            $"no 'contracts/schemas' or 'contracts/adapter' directory found above: {string.Join(", ", candidates)}");
    }
}

/// <summary>Per-route wiring for <see cref="ContractValidationFilter"/>.</summary>
public static class ContractValidationEndpointBuilderExtensions
{
    /// <summary>
    /// Applies contract validation to a route before its handler runs (00 §5.5
    /// edge). The route's body parameter must be declared <see cref="JsonDocument"/>
    /// or <see cref="JsonElement"/> so the filter receives the parse tree rather
    /// than a materialized DTO.
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint convention builder.</typeparam>
    /// <param name="builder">The route builder.</param>
    /// <param name="schemaKey">
    /// A key of the registered <see cref="ContractSchemaSet"/>, e.g.
    /// <see cref="ContractSchemaKeys.EventEnvelope"/>.
    /// </param>
    /// <exception cref="ArgumentException">Unknown <paramref name="schemaKey"/> at request time.</exception>
    public static TBuilder ValidateContractBody<TBuilder>(
        this TBuilder builder,
        string schemaKey)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(new ContractValidationFilter(schemaKey));
}
