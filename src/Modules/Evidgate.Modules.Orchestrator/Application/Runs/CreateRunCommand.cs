using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Mediator;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// Create a run from a verified manifest (<c>POST /v1/projects/{p}/runs</c>, 05.2.1 §5). The raw body travels unparsed
/// (like <see cref="Manifests.UploadManifestCommand"/>): the handler parses and validates it after the C2 pipeline
/// authorized the caller, so an anonymous or unauthorized request costs no parse.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Body">The raw UTF-8 body, at most 64 KiB.</param>
internal sealed record CreateRunCommand(Guid ProjectId, ReadOnlyMemory<byte> Body) : ICommand<Result<RunCreated>>;

/// <summary>
/// Handler of <see cref="CreateRunCommand"/> (05.2.1-T1): validate the body, load the manifest, check its suite and
/// environment revisions resolve, derive the planned executions (server-side <c>execution_key</c>), then write
/// <c>run</c> + one <c>planned_execution</c> per manifest entry + one <c>producer_slot</c> per shard (through
/// <see cref="IProducerRegistry"/>) + the <c>run.created</c> outbox row — all in the one C2 tenant transaction. Any
/// failure <see cref="Result"/> or exception leaves no row (00 §5.5 C1/C7/D2, AC2).
/// </summary>
/// <remarks>
/// [DECISION] the collection deadline is computed on the database clock (<c>now()</c> inside the INSERT), the same
/// clock <c>created_at</c> defaults to, so the two can never disagree; [DECISION] the outbox task type is
/// <c>orchestrator.run.created</c> with no handler yet — the worker's claimer filters on its handler set, so the row
/// stays <c>queued</c> until 05.3.1 registers one (01.5.1 §11); [DECISION] <c>components</c> is capped at 8 KiB
/// serialised, stricter than the column's 64 KiB CHECK; [DECISION] a manifest whose <c>environment_revision_id</c> is
/// null is refused 422 because <c>run.environment_revision_id</c> is NOT NULL.
/// </remarks>
[AuditedAction("run.create")]
[Permission(Permissions.Run.Create)]
internal sealed class CreateRunCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    ICatalogContracts catalog,
    IProducerRegistry producers,
    IOutboxWriter outbox,
    IOptions<RunsOptions> options,
    IAuditTrail audit)
    : ICommandHandler<CreateRunCommand, Result<RunCreated>>
{
    /// <summary>The largest serialised <c>components</c> object accepted (stricter than the column's 64 KiB CHECK).</summary>
    internal const int MaxComponentsBytes = 8 * 1024;

    /// <summary>The longest <c>release</c> name, in code points (Postgres <c>char_length</c> semantics).</summary>
    internal const int MaxReleaseRunes = 128;

    /// <summary>
    /// The largest absolute decimal exponent a numeric literal in <c>components</c> may carry. PostgreSQL stores
    /// <c>jsonb</c> numbers as <c>numeric</c>, whose exponent is bounded (about 131072 digits before the radix point
    /// and 16383 after); a literal such as <c>1e9999999</c> is a handful of bytes but the cast raises SQLSTATE 22003,
    /// which would surface as a 500. This bound sits far inside those limits and far above anything component
    /// metadata holds, so it refuses only values the database cannot store.
    /// </summary>
    internal const long MaxComponentNumberExponent = 1000;

    internal const string UnknownCaseRevision = "unknown_case_revision";
    internal const string UnknownSuiteRevision = "unknown_suite_revision";
    internal const string UnknownEnvironmentRevision = "unknown_environment_revision";
    internal const string InvalidVariant = "invalid_variant";
    internal const string InvalidValue = "invalid_value";

    /// <summary>The outbox task type of the create event (00 §5.3); 05.3.1 owns its handler.</summary>
    internal const string RunCreatedTaskType = "orchestrator.run.created";

    /// <summary>The planned_execution unique constraint a 23505 on the execution key names (migration 0031).</summary>
    internal const string ExecutionKeyConstraint = "planned_execution_run_execution_key";

    /// <inheritdoc />
    public async ValueTask<Result<RunCreated>> Handle(CreateRunCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(CreateRunCommand));

        if (OrchestratorScope.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<RunCreated>(mismatch);
        }

        // (1) body: parse, reject unknown members, then the field rules. Nothing is written before this passes.
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(command.Body);
        }
        catch (JsonException)
        {
            return BodyInvalid("the request body is not JSON", [new ErrorDetail(string.Empty, ApiErrorCode.RequestInvalid)]);
        }

        using var parsed = document;
        var body = parsed.RootElement;
        if (body.ValueKind != JsonValueKind.Object)
        {
            return BodyInvalid("the request body must be a JSON object", [new ErrorDetail(string.Empty, ApiErrorCode.RequestInvalid)]);
        }

        foreach (var member in body.EnumerateObject())
        {
            if (member.Name is not ("manifest_id" or "release" or "components"))
            {
                return BodyInvalid("the request body carries an unknown field", [new ErrorDetail($"/{member.Name}", "additional_properties")]);
            }
        }

        if (JsonBody.HasUnstorableText(body))
        {
            // A lone surrogate or a NUL: neither the canonical form nor jsonb can carry it.
            return BodyInvalid("the request body carries text that cannot be stored", [new ErrorDetail(string.Empty, InvalidValue)]);
        }

        if (!body.TryGetProperty("manifest_id", out var manifestElement)
            || manifestElement.ValueKind != JsonValueKind.String
            || !manifestElement.TryGetGuid(out var manifestId))
        {
            return BodyInvalid("manifest_id is required", [new ErrorDetail("/manifest_id", "required")]);
        }

        string? release = null;
        if (body.TryGetProperty("release", out var releaseElement))
        {
            if (releaseElement.ValueKind != JsonValueKind.String)
            {
                return BodyInvalid("release must be a string", [new ErrorDetail("/release", InvalidValue)]);
            }

            release = releaseElement.GetString();
            var runes = release!.EnumerateRunes().Count();
            if (runes is < 1 or > MaxReleaseRunes)
            {
                return BodyInvalid(
                    $"release must be 1..{MaxReleaseRunes} characters",
                    [new ErrorDetail("/release", InvalidValue)]);
            }
        }

        JsonElement? components = null;
        if (body.TryGetProperty("components", out var componentsElement))
        {
            if (componentsElement.ValueKind != JsonValueKind.Object)
            {
                return BodyInvalid("components must be a JSON object", [new ErrorDetail("/components", InvalidValue)]);
            }

            if (Encoding.UTF8.GetByteCount(componentsElement.GetRawText()) > MaxComponentsBytes)
            {
                return BodyInvalid(
                    $"components must be at most {MaxComponentsBytes} bytes serialised",
                    [new ErrorDetail("/components", InvalidValue)]);
            }

            // A number the jsonb column cannot hold is refused here, before any write: a literal like `1e9999999` is
            // a few bytes but Postgres refuses the cast with 22003, which would escape the tenant transaction as a 500.
            if (HasUnstorableNumber(componentsElement))
            {
                return BodyInvalid(
                    "components carries a number that cannot be stored",
                    [new ErrorDetail("/components", InvalidValue)]);
            }

            components = componentsElement;
        }

        // AC10: a syntactically valid release is still unresolvable at M1 (the bind path lands with 05.6.1).
        if (release is not null)
        {
            return Result.Failure<RunCreated>(OrchestratorErrors.ReleaseUnresolvable);
        }

        var orgId = scopes.Require().OrgId;

        // (2) the manifest, by id and project; RLS makes another tenant's invisible, so unknown and foreign are one 404.
        var manifest = await db.QuerySingleOrDefaultAsync<ManifestRow>(
            OrchestratorSql.RunManifest,
            new { org_id = orgId, project_id = command.ProjectId, manifest_id = manifestId },
            cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            return Result.Failure<RunCreated>(OrchestratorErrors.ManifestNotFound);
        }

        if (manifest.EnvironmentRevisionId is not { } environmentRevisionId)
        {
            // run.environment_revision_id is NOT NULL, so a manifest with no environment cannot create a run.
            return ManifestInvalid([new ErrorDetail("/environment_revision_id", UnknownEnvironmentRevision)]);
        }

        // (3) the suite and environment revisions must resolve in this project before any write (AC5).
        var revisions = await db.QuerySingleOrDefaultAsync<RevisionRow>(
            OrchestratorSql.RunRevisionTargets,
            new
            {
                org_id = orgId,
                project_id = command.ProjectId,
                suite_revision_id = manifest.SuiteRevisionId,
                environment_revision_id = environmentRevisionId,
            },
            cancellationToken).ConfigureAwait(false);
        if (revisions is null || !revisions.SuiteKnown)
        {
            return ManifestInvalid([new ErrorDetail("/suite_revision_id", UnknownSuiteRevision)]);
        }

        if (!revisions.EnvironmentKnown)
        {
            return ManifestInvalid([new ErrorDetail("/environment_revision_id", UnknownEnvironmentRevision)]);
        }

        // (4) derive the planned executions (server-side execution_key, shard assignment) before any write.
        var (planned, refusal) = await DeriveAsync(command.ProjectId, manifest, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return Result.Failure<RunCreated>(refusal);
        }

        var set = planned!;

        // (5) the writes, all inside the one C2 tenant transaction.
        var runId = Guid.CreateVersion7();
        var ciRun = principals.Current?.CiRun;

        await db.ExecuteAsync(
            OrchestratorSql.InsertRun,
            new
            {
                id = runId,
                org_id = orgId,
                project_id = command.ProjectId,
                manifest_id = manifestId,
                suite_revision_id = manifest.SuiteRevisionId,
                environment_revision_id = environmentRevisionId,
                ci_run_ref = ciRun is null
                    ? null
                    : JsonSerializer.Serialize(new
                    {
                        provider = ciRun.Provider,
                        repository_id = ciRun.RepositoryId,
                        run_id = ciRun.RunId,
                        run_attempt = ciRun.RunAttempt,
                    }),
                components = components?.GetRawText() ?? "{}",
                collection_timeout = options.Value.DefaultCollectionTimeout,
                late_window_minutes = options.Value.DefaultLateWindowMinutes,
            },
            cancellationToken).ConfigureAwait(false);

        try
        {
            await db.ExecuteAsync(
                OrchestratorSql.InsertPlannedExecutions,
                new { org_id = orgId, run_id = runId, rows = set.RowsJson },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UniqueViolation
            && exception.ConstraintName == ExecutionKeyConstraint)
        {
            // The in-memory check above is the cheap path; the unique index is the backstop under a race.
            return Result.Failure<RunCreated>(OrchestratorErrors.ExecutionKeyConflict(
                [new ErrorDetail("/planned_executions", ApiErrorCode.ExecutionKeyConflict)]));
        }

        // One producer_slot per manifest shard, inside this transaction (T9/T13): its commit is what makes the run
        // `queued`. A failure rolls the whole scope back (TenantTransactionBehaviour disposes it uncommitted).
        var slots = await producers.RegisterSlotsAsync(runId, set.ShardCount, cancellationToken).ConfigureAwait(false);
        if (!slots.IsSuccess)
        {
            return Result.Failure<RunCreated>(slots.Error!);
        }

        await outbox.EnqueueAsync(
            RunCreatedTaskType,
            JsonSerializer.Serialize(new { run_id = runId, project_id = command.ProjectId, manifest_id = manifestId }),
            "run",
            runId,
            $"orchestrator:run:{runId}:created",
            cancellationToken).ConfigureAwait(false);

        audit.Describe(new AuditObject("run", runId.ToString("D")));
        activity?.SetTag("evidgate.run_id", runId.ToString("D"));

        return Result.Success(new RunCreated(runId, "queued"), 201);
    }

    /// <summary>
    /// Builds the planned executions from the manifest document: case ids through Catalog, variant hashes through
    /// Catalog's normaliser (computed once per distinct params object), the server-side <c>execution_key</c>, and the
    /// shard id from the shard plan's 1-based ordinal. Any unresolved case revision or variant, or a duplicate
    /// execution key, is a refusal before any write.
    /// </summary>
    private async ValueTask<(PlannedSet? Planned, Error? Refusal)> DeriveAsync(
        Guid projectId,
        ManifestRow manifest,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(manifest.Document);
        var root = document.RootElement;
        var planned = root.GetProperty("planned_executions");
        var shards = root.GetProperty("shard_plan").GetProperty("shards");

        var count = planned.GetArrayLength();
        var caseRevisionIds = new List<Guid>(count);
        var variantParams = new List<JsonElement>(count);
        var targetEnvs = new List<string>(count);
        var shardIds = new List<string>(count);

        var ranges = new List<(int From, int To, int Ordinal)>(shards.GetArrayLength());
        var ordinal = 0;
        foreach (var shard in shards.EnumerateArray())
        {
            ordinal++;
            ranges.Add((shard.GetProperty("from_index").GetInt32(), shard.GetProperty("to_index").GetInt32(), ordinal));
        }

        var index = 0;
        foreach (var execution in planned.EnumerateArray())
        {
            caseRevisionIds.Add(execution.GetProperty("case_revision_id").GetGuid());
            variantParams.Add(execution.GetProperty("variant").GetProperty("params"));
            targetEnvs.Add(execution.GetProperty("target_env").GetString()!);
            shardIds.Add(ShardFor(ranges, index));
            index++;
        }

        var caseIds = await catalog.GetCaseIdsAsync(projectId, caseRevisionIds, cancellationToken).ConfigureAwait(false);
        var hashes = catalog.NormalizeVariantHashes(variantParams);

        var rows = new List<PlannedRow>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < count; i++)
        {
            if (!caseIds.TryGetValue(caseRevisionIds[i], out var caseId))
            {
                return (null, ManifestInvalidError(
                    [new ErrorDetail($"/planned_executions/{i}/case_revision_id", UnknownCaseRevision, i)]));
            }

            if (hashes[i] is not { } variantHash)
            {
                return (null, ManifestInvalidError(
                    [new ErrorDetail($"/planned_executions/{i}/variant/params", InvalidVariant, i)]));
            }

            var key = ExecutionKey.Compute(caseId, variantHash, targetEnvs[i]);
            if (!seen.Add(key))
            {
                return (null, OrchestratorErrors.ExecutionKeyConflict(
                    [new ErrorDetail($"/planned_executions/{i}", ApiErrorCode.ExecutionKeyConflict, i)]));
            }

            rows.Add(new PlannedRow(Guid.CreateVersion7(), key, caseRevisionIds[i], variantHash, targetEnvs[i], shardIds[i]));
        }

        return (new PlannedSet(JsonSerializer.Serialize(rows), ranges.Count), null);
    }

    /// <summary>The shard id of the planned execution at <paramref name="index"/>: <c>shard-&lt;1-based ordinal&gt;</c> (T54).</summary>
    private static string ShardFor(List<(int From, int To, int Ordinal)> ranges, int index)
    {
        foreach (var range in ranges)
        {
            if (index >= range.From && index < range.To)
            {
                return $"shard-{range.Ordinal}";
            }
        }

        // The manifest verifier guarantees the shards partition [0, count), so this is unreachable for a verified manifest.
        throw new InvalidOperationException($"planned execution {index} is not inside any shard of the manifest");
    }

    /// <summary>True when any number anywhere inside <paramref name="element"/> has an exponent jsonb cannot hold.</summary>
    private static bool HasUnstorableNumber(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => NumberOverflows(element.GetRawText()),
        JsonValueKind.Array => element.EnumerateArray().Any(HasUnstorableNumber),
        JsonValueKind.Object => element.EnumerateObject().Any(member => HasUnstorableNumber(member.Value)),
        _ => false,
    };

    /// <summary>
    /// True when the JSON number token <paramref name="raw"/> (already accepted by <see cref="JsonDocument.Parse"/>)
    /// carries a decimal exponent outside <see cref="MaxComponentNumberExponent"/>, or one too large to parse. A token
    /// with no exponent, or a small one, is representable: the 8 KiB <c>components</c> cap bounds its digit count far
    /// below jsonb's 131072-digit limit.
    /// </summary>
    private static bool NumberOverflows(string raw)
    {
        var marker = raw.AsSpan().IndexOfAny('e', 'E');
        if (marker < 0)
        {
            return false;
        }

        return !long.TryParse(raw.AsSpan(marker + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exponent)
               || exponent < -MaxComponentNumberExponent
               || exponent > MaxComponentNumberExponent;
    }

    private static Result<RunCreated> BodyInvalid(string message, IReadOnlyList<ErrorDetail> details) =>
        Result.Failure<RunCreated>(Error.Invalid(message, details));

    private static Result<RunCreated> ManifestInvalid(IReadOnlyList<ErrorDetail> details) =>
        Result.Failure<RunCreated>(ManifestInvalidError(details));

    private static Error ManifestInvalidError(IReadOnlyList<ErrorDetail> details) =>
        OrchestratorErrors.ManifestInvalid("the manifest cannot be used to create a run", details);

    /// <summary>The manifest row the handler derives from (Dapper maps the snake_case columns).</summary>
    private sealed class ManifestRow
    {
        public Guid SuiteRevisionId { get; set; }

        public Guid? EnvironmentRevisionId { get; set; }

        public string Document { get; set; } = string.Empty;
    }

    /// <summary>Whether the manifest's suite and environment revisions resolve in the project.</summary>
    private sealed class RevisionRow
    {
        public bool SuiteKnown { get; set; }

        public bool EnvironmentKnown { get; set; }
    }

    /// <summary>The one <c>jsonb</c> batch parameter of the planned-execution insert, plus the shard count for the slot registration.</summary>
    private sealed record PlannedSet(string RowsJson, int ShardCount);

    /// <summary>One planned execution as the <c>jsonb_to_recordset</c> batch reads it (the wire names are the column names).</summary>
    private sealed record PlannedRow(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("execution_key")] string ExecutionKey,
        [property: JsonPropertyName("case_revision_id")] Guid CaseRevisionId,
        [property: JsonPropertyName("variant_hash")] string VariantHash,
        [property: JsonPropertyName("target_env")] string TargetEnv,
        [property: JsonPropertyName("shard_id")] string ShardId);
}
