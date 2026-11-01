using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Migrator;

/// <summary>
/// The environment half of <c>seed-dev</c>: one <c>dev</c> environment revision in <c>dev-project</c>, so a manifest and
/// a run have a real <c>environment_revision_id</c> to name.
/// </summary>
/// <remarks>
/// A run refuses <c>environment_revision_id: null</c> as <c>unknown_environment_revision</c>, and the seeded ingest token
/// carries no <c>environment:write</c>, so the revision has to exist before a developer can create a run. The content is
/// fixed and secret-free (a loopback base URL, no dependency versions, no secret references); the lookup reads the
/// environment's current revision and compares the module's own content hash, so a rerun returns the same id, number and
/// hash and writes nothing, and a revision is never composed here.
/// </remarks>
internal static class DevSeedEnvironment
{
    /// <summary>The environment key a manifest and a run name, and the <c>{e}</c> of the revision route.</summary>
    internal const string EnvironmentKey = "dev";

    /// <summary>The stdout prefix of the line that carries the revision id a manifest names.</summary>
    internal const string ManifestLine = "seed-dev: manifest inputs";

    /// <summary>Idempotency key of the create: a replay returns the stored answer instead of a second revision.</summary>
    private const string IdempotencyKey = "dev-seed-environment";

    /// <summary>
    /// The sample <c>environment-revision.v1</c> body: a loopback base URL a planned execution can reach on the dev
    /// machine, no result-affecting dependency versions, and no secret references. Fixed, so the content address is stable.
    /// </summary>
    private const string SampleBody =
        """{"name":"dev","base_urls":{"local":"http://127.0.0.1:8080"},"dependency_versions":{},"secret_refs":[]}""";

    /// <summary>The environment's current revision, or null when the environment or its pointer is absent.</summary>
    private const string PointerSql =
        """
        select e.current_revision_id, r.revision_no, r.revision_hash
          from public.environment e
          left join public.environment_revision r on r.org_id = e.org_id and r.id = e.current_revision_id
         where e.project_id = @project_id
           and e.environment_key = @environment_key
        """;

    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Returns the environment revision id a manifest names, creating the sample revision when it is absent; null with the
    /// exit code when the module refuses it.
    /// </summary>
    /// <param name="mediator">The scoped mediator inside the tenant scope and the synthetic principal.</param>
    /// <param name="services">The same scope, for the tenant-scoped lookup.</param>
    /// <param name="output">Where the revision line and the manifest inputs line go.</param>
    /// <param name="error">Where a refusal or failure goes.</param>
    internal static async Task<(Guid? RevisionId, int ExitCode)> WriteAsync(
        IMediator mediator,
        IServiceProvider services,
        TextWriter output,
        TextWriter error)
    {
        var body = Encoding.UTF8.GetBytes(SampleBody);
        var request = JsonSerializer.Deserialize<CreateEnvironmentRevisionRequest>(body, RequestJson)!;

        // The module's own rules decide the content identity; a configuration they refuse is a seed defect, not a user error.
        var (configuration, details) = EnvironmentRevisionRules.Check(request);
        if (configuration is null)
        {
            await error.WriteLineAsync(
                $"E seed-dev: the sample environment revision was refused by the module's rules ({string.Join(", ", details.Select(detail => detail.Reason))})").ConfigureAwait(false);

            return (null, MigratorExitCodes.MigrationFailed);
        }

        var current = await CurrentRevisionAsync(services).ConfigureAwait(false);
        if (current is { RevisionHash: { } hash, CurrentRevisionId: { } currentId } && string.Equals(hash, configuration.RevisionHash, StringComparison.Ordinal))
        {
            await ReportAsync(output, currentId, current.RevisionNo, hash, created: false).ConfigureAwait(false);

            return (currentId, MigratorExitCodes.Success);
        }

        var created = await mediator
            .Send(new CreateEnvironmentRevisionCommand(DevSeed.ProjectId, EnvironmentKey, body, BodyHash(body), IdempotencyKey))
            .ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            await error.WriteLineAsync(
                $"E seed-dev: the dev environment revision was refused ({created.Error!.Code}): {created.Error.Message}").ConfigureAwait(false);

            return (null, MigratorExitCodes.MigrationFailed);
        }

        var revision = created.Value;
        await ReportAsync(output, revision.RevisionId, revision.RevisionNo, revision.RevisionHash, revision.Created).ConfigureAwait(false);

        return (revision.RevisionId, MigratorExitCodes.Success);
    }

    /// <summary>The environment's current revision, read under row level security in a read-only tenant scope.</summary>
    private static async Task<PointerRow?> CurrentRevisionAsync(IServiceProvider services)
    {
        var scopes = services.GetRequiredService<ITenantScopeFactory>();
        var db = services.GetRequiredService<ITenantDb>();

        await using var scope = await scopes
            .BeginAsync(DevSeed.OrgId, DevSeed.ProjectId, readOnlyTransaction: true)
            .ConfigureAwait(false);

        return await db
            .QuerySingleOrDefaultAsync<PointerRow>(PointerSql, new { project_id = DevSeed.ProjectId, environment_key = EnvironmentKey })
            .ConfigureAwait(false);
    }

    /// <summary>Hex SHA-256 of the body, the value the command's idempotency store compares.</summary>
    private static string BodyHash(byte[] body) => Convert.ToHexStringLower(SHA256.HashData(body));

    /// <summary>The revision line and the <c>environment_revision_id</c> a manifest names.</summary>
    private static async Task ReportAsync(TextWriter output, Guid revisionId, int? revisionNo, string hash, bool created)
    {
        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"seed-dev: environment {EnvironmentKey} revision {revisionNo?.ToString(CultureInfo.InvariantCulture) ?? "<unknown>"} {hash} ({(created ? "created" : "already present")})")).ConfigureAwait(false);

        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"{ManifestLine} environment_revision_id={revisionId} environment_key={EnvironmentKey}")).ConfigureAwait(false);
    }

    private sealed class PointerRow
    {
        public Guid? CurrentRevisionId { get; set; }

        public int? RevisionNo { get; set; }

        public string? RevisionHash { get; set; }
    }
}
