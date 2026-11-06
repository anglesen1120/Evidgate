using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Application.Freeze;
using Evidgate.Modules.Evidence.Application.Retention;
using Evidgate.Modules.Evidence.Application.Evaluation;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Ingestion.Contracts;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Evidence;

/// <summary>
/// Composition seam of the Evidence module (01.1.2-T2.1); internal by design and
/// reached through <c>InternalsVisibleTo</c> (00 §5.5).
/// </summary>
internal static class EvidenceModule
{
    /// <summary>Registers the module's services in the calling host.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">
    /// The host's configuration. Nothing in this shared registration is decided by it today: the one switch that used
    /// to be read here (<c>EVIDGATE__PARSE__OUTOFPROCESS</c>) now lives in the Worker-only
    /// <c>ParseRunnerServiceCollectionExtensions.AddParseRunner</c>, because that registration is a process-spawn
    /// capability the Api must not hold. Every other module setting is bound lazily through
    /// <c>BindConfiguration</c>, like <c>EVIDGATE__OBJECT__*</c>.
    /// </param>
    public static IServiceCollection AddEvidenceModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        RegisterArtifacts(services);
        RegisterArtifactOutboxHandlers(services);
        RegisterParsePath(services);
        RegisterEvidenceReads(services);

        return services;
    }

    /// <summary>The artifact pipeline: the write model, the two commands' validators and core, the object store,
    /// the completion verifier, the download gate and the capture options read.</summary>
    /// <param name="services">The host's service collection.</param>
    private static void RegisterArtifacts(IServiceCollection services)
    {
        // The EF write model of artifacts and their kind policies (00 §5.5 C4).
        services.AddEvidgateDbContext<EvidenceDbContext>(typeof(EvidenceDbContext).Assembly);

        // C2 stage-2 shape validation of the mint command (resolved by ValidationBehaviour).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidator<CreateArtifactRecord>, CreateArtifactRecordValidator>());

        // C2 stage-2 shape validation of the upload-url command.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidator<RequestArtifactUploadUrl>, RequestArtifactUploadUrlValidator>());

        // The mint core shared by both artifact commands.
        services.TryAddScoped<ArtifactMinter>();

        // The S3-compatible presign adapter, bound from EVIDGATE__OBJECT__* (00 §9.3).
        services.AddEvidgateObjectStorage();

        // C2 stage-2 shape validation of the completion command.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidator<CompleteArtifactUpload>, CompleteArtifactUploadValidator>());

        // The endpoint-side verification: one row read in a read-only scope, then the
        // HEAD/copy with no transaction open, so the writer command's transaction only ever wraps the commit.
        services.TryAddScoped<ArtifactCompletionVerifier>();

        // The download route's endpoint-side decision: it reads the caller's sensitivity permission with the row, and
        // writes the denial's audit row in its own committed scope before the command transaction exists.
        services.TryAddScoped<Application.Artifacts.ArtifactDownloadGate>();

        // C2 stage-2 shape validation of the per-kind policy override.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidator<Application.Artifacts.SetArtifactKindPolicyOverride>, Application.Artifacts.SetArtifactKindPolicyOverrideValidator>());

        // The capture options read shared by the route that serves them and the run-create path: one cross-module read
        // over the caller's tenant scope, so both answers come from the same statements.
        services.TryAddScoped<ICaptureOptionsReader, Application.Capture.CaptureOptionsReader>();
    }

    /// <summary>The background work the artifact commands enqueue: the staging-object delete and the secret scan.</summary>
    /// <param name="services">The host's service collection.</param>
    private static void RegisterArtifactOutboxHandlers(IServiceCollection services)
    {
        // The staging-object delete the completion enqueues. Registered in both hosts like every module
        // piece; only the Worker runs a consumer, and the Api host tolerates the row until it does.
        services.AddTaskHandler<ArtifactStagingDeletePayload, ArtifactStagingDeleteTaskHandler>();

        // The masking scan the completion enqueues for every ready artifact. Registered transactionless
        // because the object read must run with no database transaction open (00 §5.7 P5): the handler opens a short
        // read scope, reads the object and scans it, then opens the write scope that commits the verdict.
        services.AddTransactionlessTaskHandler<RunArtifactSecretScanPayload, RunArtifactSecretScanHandler>();
    }

    /// <summary>The report path both hosts compose: the parser registry, the mapper, the producer kind read and the
    /// one untrusted-parser profile. The runner that consumes them is Worker-only, so it stays out.</summary>
    /// <param name="services">The host's service collection.</param>
    private static void RegisterParsePath(IServiceCollection services)
    {
        // The JUnit mapping path. The registry read is a singleton because the handler that consumes it is one (a
        // singleton must never take a scoped service); the mapper is a singleton because its only state is the untrusted-parser profile and the report byte ceiling. The task handler
        // is registered with the parse runner (Worker-only), because it needs the child process; it is
        // transactionless because its bytes are fetched and mapped before any write scope opens (P5).
        services.TryAddSingleton<IReportParserResolver, ReportParserResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJUnitMapper, PytestJUnitMapper>());

        // The producer kind a raw report is parsed under is the run's manifest declaration, read inside the
        // completion's own tenant transaction; scoped like every reader bound to the ambient scope.
        services.TryAddScoped<IRawReportProducerKindReader, RawReportProducerKindReader>();

        // The one untrusted-parser profile (00 §5.7 S5), published on the module's .Contracts.
        // It has no consumer yet: the `complete` verifier uses the static MediaSniffer directly,
        // and the report mapper is the first IParsing consumer, resolving this same singleton instead of
        // building its own readers (00 §5.7 S4). The report entry cap is the host's configured one, so a
        // deployment that publishes a lower cap refuses over-large reports here as well as in the child.
        services.TryAddSingleton<IParsing>(provider =>
            new ParsingService(provider.GetRequiredService<IOptions<IngestionLimits>>().Value.MaxReportCases));
    }

    /// <summary>The read side other modules call, and the options both hosts must bind whatever they run.</summary>
    /// <param name="services">The host's service collection.</param>
    private static void RegisterEvidenceReads(IServiceCollection services)
    {
        // The one cross-module call the reconciler makes to freeze a run's evidence: the
        // ready set with (execution_key, attempt_no) resolved to case_attempt, the requirement gaps and the canonical
        // hash. Scoped like every other module service; it takes the ambient scope through ITenantDb and never opens
        // a connection or a transaction of its own (00 §5.5 C3/C7).
        services.TryAddScoped<IEvidenceFreeze, EvidenceFreeze>();

        // The read side of a frozen revision for the evaluator: the revision row and the latest attempt each execution
        // key had when it froze. Scoped like every other module service and bound to the ambient tenant scope.
        services.TryAddScoped<IFrozenEvidence, FrozenEvidence>();

        // The out-of-process parse runner is deliberately NOT registered here. It spawns processes, so
        // only the Worker composes it, through ParseRunnerServiceCollectionExtensions.AddParseRunner (the review found:
        // the Api process must not hold a process-spawn capability). This shared registration is what both hosts call,
        // so a runner registered here would be resolvable from the Api again.

        // The runner's options, bound in BOTH hosts because `complete` reads the one switch that decides
        // whether a raw report schedules its parse rows (`EVIDGATE__PARSE__OUTOFPROCESS`) — the parse of its bytes and
        // the mapping of its records — the Api commits those rows even though only the Worker runs them. Binding them
        // registers no capability: the runner and its task handlers stay in AddParseRunner, Worker-only and flag-on.
        services.AddOptions<ParseRunnerOptions>().BindConfiguration(ParseRunnerOptions.SectionName);

        // The retention window's floor, ceiling and platform defaults, owner-configurable per deployment. Bound in
        // both hosts: the read route serves the defaults and the write route enforces the range. The cross-field check
        // refuses a floor above its ceiling or a default the floor would reject, which would otherwise make every
        // default-served project's first write fail at runtime.
        services.AddOptions<RetentionLimits>()
            .BindConfiguration(RetentionLimits.SectionName)
            .ValidateDataAnnotations()
            .Validate(
                limits => limits.IsCoherent,
                $"EVIDGATE__RETENTION__* must satisfy min <= max for both windows and keep both defaults inside the range ({RetentionLimits.SectionName})")
            .ValidateOnStart();
    }
}
