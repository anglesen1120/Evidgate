using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Application.Freeze;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Evidence.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Evidence;

/// <summary>
/// Composition seam of the Evidence module (01.1.2-T2.1); internal by design and
/// reached through <c>InternalsVisibleTo</c> (00 §5.5).
/// </summary>
internal static class EvidenceModule
{
    /// <summary>Registers the module's services in the calling host.</summary>
    public static IServiceCollection AddEvidenceModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 06.1.1-T1: the EF write model of artifacts and their kind policies (00 §5.5 C4).
        services.AddEvidgateDbContext<EvidenceDbContext>(typeof(EvidenceDbContext).Assembly);

        // 06.1.1-T2: C2 stage-2 shape validation of the mint command (resolved by ValidationBehaviour).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidator<CreateArtifactRecord>, CreateArtifactRecordValidator>());

        // 06.1.2-T1: C2 stage-2 shape validation of the upload-url command.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidator<RequestArtifactUploadUrl>, RequestArtifactUploadUrlValidator>());

        // 06.1.2-T1: the mint core shared by both artifact commands (06.1.1-T2 and 06.1.2-T1).
        services.TryAddScoped<ArtifactMinter>();

        // 06.1.2-T1: the S3-compatible presign adapter, bound from EVIDGATE__OBJECT__* (00 §9.3).
        services.AddEvidgateObjectStorage();

        // 06.1.2-T2: C2 stage-2 shape validation of the completion command.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidator<CompleteArtifactUpload>, CompleteArtifactUploadValidator>());

        // 06.1.2-T2: the endpoint-side verification (T86 steps 1-4): one row read in a read-only scope, then the
        // HEAD/copy with no transaction open, so the writer command's transaction only ever wraps the commit.
        services.TryAddScoped<ArtifactCompletionVerifier>();

        // 06.1.2-T2: the staging-object delete the completion enqueues. Registered in both hosts like every module
        // piece; only the Worker runs a consumer, and the Api host tolerates the row until it does (T19).
        services.AddTaskHandler<ArtifactStagingDeletePayload, ArtifactStagingDeleteTaskHandler>();

        // 06.3.1-T1.2: the one untrusted-parser profile (00 §5.7 S5), published on the module's .Contracts.
        // It has no consumer yet: the `complete` verifier uses the static MediaSniffer directly (06.3.1-T1),
        // and 04.4.1's report mapper is the first IParsing consumer, resolving this same singleton instead of
        // building its own readers (00 §5.7 S4).
        services.TryAddSingleton<IParsing, ParsingService>();

        // 06.5.1-T1/T2: the one cross-module call 05's reconciler (05.3.2-T2) makes to freeze a run's evidence: the
        // ready set with (execution_key, attempt_no) resolved to case_attempt, the requirement gaps and the canonical
        // hash. Scoped like every other module service; it takes the ambient scope through ITenantDb and never opens
        // a connection or a transaction of its own (00 §5.5 C3/C7).
        services.TryAddScoped<IEvidenceFreeze, EvidenceFreeze>();

        return services;
    }
}
