using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Evidgate.Modules.Policy.Contracts;
using Mediator;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// Create a run from a verified manifest (<c>POST /v1/projects/{p}/runs</c>, 05.2.1 §5). The raw body travels unparsed
/// (like <see cref="Manifests.UploadManifestCommand"/>): the handler parses and validates it after the C2 pipeline
/// authorized the caller, so an anonymous or unauthorized request costs no parse.
/// <para>
/// Besides <c>manifest_id</c>, <c>components</c> and an optional <c>release_id</c>, the body may carry the legacy
/// <c>release</c> name — permanently unresolvable, because a run binds to a release by id — plus <c>purpose</c>
/// (<c>regular</c>, the default, or <c>replay</c>) and <c>replay_grant_id</c> (05.2.1-T5): a <c>replay</c> request is
/// refused 422 before any read, and a <c>replay_grant_id</c> is only meaningful for one.
/// </para>
/// <para>
/// The body may also carry <c>ref</c> (05.2.2-T1.3): <c>{kind, name, pr_number, head_sha, base_sha, trigger_event,
/// is_fork}</c>, the identity a run records when no verified GitHub OIDC claim covers it. The block is always
/// shape-checked before any write (AC4), and where a verified claim covers a member the body's value is ignored rather
/// than refused (AC3).
/// </para>
/// <para>
/// Idempotent under the client's <c>Idempotency-Key</c> (00 §9.1, 05.2.1-T2): the C2 idempotency stage claims the key
/// inside the run's tenant transaction, so the <c>idempotency_record</c>, the run, its planned executions, its producer
/// slots and its outbox row commit or roll back together. The request identity is the RFC 8785 digest of the body
/// (<see cref="BodyHash"/>), so member order and whitespace are invisible to dedup: a retry with the same key and the
/// same canonical body replays the original response, a different body is 409 <c>idempotency_conflict</c>.
/// </para>
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Body">The raw UTF-8 body, at most 64 KiB — the run-create transport cap the endpoint counts.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
/// <param name="CiRunBinding">
/// The caller's verified CI run reference in the <see cref="CiRunReference.ToBindingReference"/> form
/// (<c>{repository_id}/{run_id}/{run_attempt}</c>, T88), or null for a caller whose token carries none (a member, an
/// ingest-token exchange). 05.2.2-T1.4 folds it into <see cref="BodyHash"/>: the identity a run records now depends on
/// it, so the same key with the same body but a different CI run attempt must conflict instead of replaying. Never
/// serialized — the request document the store hashes is the body digest, exactly as before (00 §9.1).
/// </param>
internal sealed record CreateRunCommand(
    Guid ProjectId,
    [property: JsonIgnore] ReadOnlyMemory<byte> Body,
    string IdempotencyKey,
    [property: JsonIgnore] string? CiRunBinding = null)
    : ICommand<Result<RunCreated>>, IIdempotentCommand
{
    /// <summary>The 00 §7 operation the key is claimed for (05.2.1-T2.1): stable across a rename.</summary>
    string IIdempotentCommand.Operation => "run:create";

    /// <summary>
    /// The request identity the idempotency store hashes, computed from <see cref="Body"/> on first serialization
    /// (inside the C2 idempotency stage, never at the HTTP edge): the RFC 8785 digest of the body
    /// (<see cref="CanonicalHash"/>), so member order and whitespace differences replay rather than conflict. A body
    /// that cannot be canonicalized — invalid JSON, a duplicate member name, a lone surrogate — falls back to
    /// <c>raw-sha256:&lt;hex&gt;</c> of the raw bytes, so the request still reaches the handler, which answers its normal
    /// 400 <c>request_invalid</c> (a failure Result is never stored).
    /// <para>
    /// 05.2.2-T1.4: a caller with a verified CI run reference (<see cref="CiRunBinding"/>) folds it in, so the same
    /// key and body from a different CI run attempt is 409 <c>idempotency_conflict</c> rather than a replay, while a
    /// re-minted token for the same attempt still replays. The fold is additive: a request without that reference
    /// hashes exactly as before this story, so no stored record mass-conflicts on deploy (00 §9.1).
    /// </para>
    /// </summary>
    public string BodyHash => CiRunBinding is { Length: > 0 } binding
        ? Combine(ComputeBodyHash(Body), binding)
        : ComputeBodyHash(Body);

    /// <summary>
    /// The digest of a request that carries a verified CI run reference: the RFC 8785 digest of one two-member document
    /// holding the body's own digest and the reference, so both halves are covered by a single canonical hash (the
    /// reference is escaped as a JSON string, not concatenated).
    /// </summary>
    private static string Combine(string bodyHash, string binding) =>
        CanonicalHash.Compute(
            $$"""{"body_hash":{{JsonSerializer.Serialize(bodyHash)}},"ci_run_ref":{{JsonSerializer.Serialize(binding)}}}""");

    private static string ComputeBodyHash(ReadOnlyMemory<byte> body)
    {
        try
        {
            return CanonicalHash.Compute(body.Span);
        }
        catch (JsonCanonicalizationException)
        {
            return RawSha256(body);
        }
        catch (JsonException)
        {
            return RawSha256(body);
        }
    }

    private static string RawSha256(ReadOnlyMemory<byte> body) =>
        "raw-sha256:" + Convert.ToHexStringLower(SHA256.HashData(body.Span));
}

/// <summary>
/// Handler of <see cref="CreateRunCommand"/> (05.2.1-T1): validate the body, load the manifest, check its suite and
/// environment revisions resolve, derive the planned executions (server-side <c>execution_key</c>), resolve the active
/// policy revision the run pins (07.1.1-T4.2, through <see cref="IPolicyContracts"/>), then write <c>run</c> + one
/// <c>planned_execution</c> per manifest entry + one <c>producer_slot</c> per shard (through
/// <see cref="IProducerRegistry"/>) + the <c>run.created</c> outbox row — all in the one C2 tenant transaction. Any
/// failure <see cref="Result"/> or exception leaves no row (00 §5.5 C1/C7/D2, AC2). The C2 idempotency stage claims the
/// <c>Idempotency-Key</c> in that same transaction (05.2.1-T2), so the <c>idempotency_record</c> commits or rolls back
/// with the run and a replayed request writes nothing.
/// </summary>
/// <remarks>
/// [DECISION] the collection deadline is computed on the database clock (<c>now()</c> inside the INSERT), the same
/// clock <c>created_at</c> defaults to, so the two can never disagree; [DECISION] the outbox task type is
/// <c>orchestrator.run.created</c> with no handler yet — the worker's claimer filters on its handler set, so the row
/// stays <c>queued</c> until 05.3.1 registers one (01.5.1 §11); [DECISION] <c>components</c> is capped at 8 KiB
/// serialised, stricter than the column's 64 KiB CHECK; [DECISION] a manifest whose <c>environment_revision_id</c> is
/// null is refused 422 because <c>run.environment_revision_id</c> is NOT NULL; [DECISION] the pinned policy values are
/// read once, before the run INSERT, and written once — nothing updates <c>run.policy_revision_id</c>,
/// <c>collection_deadline_at</c> or <c>late_window_minutes</c> afterwards (FR-07-2), so a later activation never moves
/// an existing run's pin. [DECISION] the <c>purpose: "replay"</c> path is inert until 08.4.3: no replay-grant reader
/// exists (<c>Evidgate.Modules.Healing.Contracts</c> declares only an empty <c>IHealing</c>), so the handler refuses
/// every replay request 422 (<c>replay_release_not_allowed</c> first, then <c>replay_grant_invalid</c>) and adds no
/// reader of its own; the columns (<c>run.purpose</c>, <c>run.replay_grant_id</c>, migration 0031) stay at their
/// defaults and are immutable because the app role holds no UPDATE on them (05.3.1 owns the read model and MUST NOT
/// grant UPDATE on <c>purpose</c>/<c>replay_grant_id</c>). [DECISION] the verified identity of a run is the caller's
/// <c>ci_token_issue</c> row — written by the 02.4.1 exchange from a token it already verified — read by the run
/// reference the minted token carries (<c>repository_id</c>, <c>run_id</c>, <c>run_attempt</c>) inside this same
/// tenant transaction, and only when the principal carries a <see cref="CiRunReference"/>; the newest row for the
/// attempt wins because its <c>ref</c>/<c>sha</c> are fixed per attempt (owner decision D-S45-Q2, T1.3). A missing or
/// foreign-project row means the caller is not verified, so body values are stored and labelled rather than trusted
/// (05.2.2 AC1–AC3). [DECISION] a member a verified claim covers
/// is ignored in the body rather than refused (AC3), the claim's <c>ref</c> shape is re-checked here so a value the
/// column CHECKs refuse is dropped instead of surfacing as a 500, and <c>ref_name</c>/the shas are never logged (only
/// the run id and <c>ref_kind</c>, 05.2.2 §8).
/// </remarks>
[AuditedAction("run.create")]
[Permission(Permissions.Run.Create)]
internal sealed class CreateRunCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    ICatalogContracts catalog,
    IPolicyContracts policies,
    IProducerRegistry producers,
    IOutboxWriter outbox,
    IOptions<RunsOptions> options,
    IOptions<IngestionLimits> ingestionLimits,
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

    /// <summary>The default run <c>purpose</c> when the body omits it: a fresh run (05.2.1-T5).</summary>
    internal const string RegularPurpose = "regular";

    /// <summary>A run that replays a stored run's inputs (05.2.1-T5); refused until the grant store lands with 08.4.3.</summary>
    internal const string ReplayPurpose = "replay";

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

        if (UnstorableJsonText.HasUnstorableText(body))
        {
            // Checked before any member name is read (a lone-surrogate name throws on read): a lone surrogate or a NUL: neither the canonical form nor jsonb can carry it.
            return BodyInvalid("the request body carries text that cannot be stored", [new ErrorDetail(string.Empty, InvalidValue)]);
        }

        foreach (var member in body.EnumerateObject())
        {
            if (member.Name is not ("manifest_id" or "release" or "release_id" or "components" or "purpose" or "replay_grant_id" or "ref"))
            {
                return BodyInvalid("the request body carries an unknown field", [new ErrorDetail($"/{member.Name}", "additional_properties")]);
            }
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

        // (1b) the replay members (05.2.1-T5): purpose defaults to `regular` and replay_grant_id is meaningful only for
        // a replay run. Both are parsed here, after the release check and before any database read, and a replay request
        // is refused by the ordered checks below before the regular path (components, release_unresolvable) runs.
        var purpose = RegularPurpose;
        if (body.TryGetProperty("purpose", out var purposeElement))
        {
            if (purposeElement.ValueKind != JsonValueKind.String)
            {
                return BodyInvalid("purpose must be a string", [new ErrorDetail("/purpose", InvalidValue)]);
            }

            purpose = purposeElement.GetString()!;
            if (purpose is not (RegularPurpose or ReplayPurpose))
            {
                return BodyInvalid(
                    $"purpose must be \"{RegularPurpose}\" or \"{ReplayPurpose}\"",
                    [new ErrorDetail("/purpose", InvalidValue)]);
            }
        }

        Guid? replayGrantId = null;
        if (body.TryGetProperty("replay_grant_id", out var grantElement))
        {
            if (grantElement.ValueKind != JsonValueKind.String || !grantElement.TryGetGuid(out var grantId))
            {
                return BodyInvalid("replay_grant_id must be a UUID", [new ErrorDetail("/replay_grant_id", InvalidValue)]);
            }

            replayGrantId = grantId;
        }

        if (purpose == ReplayPurpose)
        {
            // A replay run re-executes a stored run's inputs, so it may not also name a release — by legacy name or by
            // id; this refusal wins over the grant check below. The member is checked on the body, before its shape.
            if (release is not null || body.TryGetProperty("release_id", out _))
            {
                return Result.Failure<RunCreated>(OrchestratorErrors.ReplayReleaseNotAllowed);
            }

            // AC8: the replay_grant store lands with 08.4.3, so no grant resolves at this milestone — with or without a
            // well-formed replay_grant_id — and the request is refused before any manifest read. The id is still parsed
            // above, so a malformed one is request_invalid rather than this code.
            return Result.Failure<RunCreated>(OrchestratorErrors.ReplayGrantInvalid);
        }

        if (replayGrantId is not null)
        {
            // A grant id is meaningful only for a replay run.
            return BodyInvalid("replay_grant_id is only valid for a replay run", [new ErrorDetail("/replay_grant_id", InvalidValue)]);
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

        // (1b2) the release the run wants to be bound to. Shape only here — the uuid and nothing else — because an
        // unknown, foreign or draft release is a database answer (404/422) that needs the tenant scope below.
        var (releaseId, releaseIdError) = RunReleaseBinding.ParseReleaseId(body);
        if (releaseIdError is not null)
        {
            return Result.Failure<RunCreated>(releaseIdError);
        }

        // (1c) the run-identity block (05.2.2-T1.3, AC4): shape-checked here — before the database reads below and long
        // before the write — whether or not a verified claim later overrides one of its members. An invalid member is
        // 422 request_invalid naming its pointer, exactly like the rest of this body parse.
        var (bodyRef, refErrors) = RunIdentityRules.ParseBodyRef(body);
        if (refErrors.Count > 0)
        {
            return BodyInvalid("the request body carries an invalid ref", refErrors);
        }

        // The legacy `release` name stays permanently unresolvable: a release is addressed by id, and a name would make
        // the binding depend on a mutable lookup rather than the immutable row the caller chose.
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
        var (planned, refusal) = await DeriveAsync(command.ProjectId, manifest, ingestionLimits.Value, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return Result.Failure<RunCreated>(refusal);
        }

        var set = planned!;

        // (5) the policy revision the run pins (07.1.1-T4.2, FR-07-2). The active revision's id is stored on
        // run.policy_revision_id, and the two values its body sets replace the EVIDGATE:Runs defaults; a project with no
        // active revision keeps the defaults and a null pin. The read is one lock-free statement inside this same tenant
        // transaction (finding F4): it takes no project row lock, so this run create never holds the row a concurrent
        // activation locks FOR NO KEY UPDATE, and a pin of the revision active at this transaction's commit is the
        // expected READ COMMITTED ordering.
        var activePolicy = await policies.GetActivePolicyAsync(command.ProjectId, cancellationToken).ConfigureAwait(false);
        var collectionTimeout = activePolicy?.CollectionTimeoutMinutes is { } policyTimeout
            ? TimeSpan.FromMinutes(policyTimeout)
            : options.Value.DefaultCollectionTimeout;
        var lateWindowMinutes = activePolicy?.LateWindowMinutes ?? options.Value.DefaultLateWindowMinutes;

        // (5b) the identity this run records (05.2.2-T1.3, AC1–AC3, T1.4). The only VERIFIED source is the
        // ci_token_issue row the 02.4.1 exchange wrote from the GitHub OIDC token it had already verified, found by the
        // run reference the minted token carries (repository/run/attempt) rather than by the token's jti, which the
        // exchange mints fresh and unrelated: reachable only for a principal that carries a CiRunReference, so a member
        // or an ingest-token caller can never be verified. The read is one statement inside this same tenant
        // transaction; a missing row (purged, another project's, or another run attempt's) leaves the caller unverified
        // rather than trusted. Reads only — nothing is written before the identity passes the effective-value check.
        var principal = principals.Current;
        var claim = await ReadVerifiedClaimAsync(principal, command.ProjectId, orgId, cancellationToken).ConfigureAwait(false);
        var identity = RunIdentityRules.Resolve(bodyRef, claim);
        if (RunIdentityRules.EffectiveViolation(identity) is { } violation)
        {
            // The body-only path: a `pull_request` kind without a number is not a usable identity (AC4).
            return BodyInvalid("the run identity is invalid", [violation]);
        }

        // (5c) a fork run's identity is advisory-only, so it may not be pinned to a release; the refusal runs before
        // the release read and long before the writes, so a fork request leaves nothing behind.
        if (identity.IsFork && releaseId is not null)
        {
            return Result.Failure<RunCreated>(OrchestratorErrors.ForkRunNotBindable);
        }

        // (5d) the release the run asks to be bound to: resolved inside this project before any write, so an unknown,
        // foreign or draft release leaves no run, execution or slot behind. A release that resolves but shares no
        // digest never refuses the run — the binding is partial or empty, and the mismatch is read back from the pair.
        var (bindReleaseId, releaseError) = await RunReleaseBinding
            .ResolveAsync(releaseId, command.ProjectId, orgId, db, cancellationToken)
            .ConfigureAwait(false);
        if (releaseError is not null)
        {
            return Result.Failure<RunCreated>(releaseError);
        }

        // (6) the writes, all inside the one C2 tenant transaction.
        var runId = Guid.CreateVersion7();
        var ciRun = principal?.CiRun;

        await db.ExecuteAsync(
            OrchestratorSql.InsertRun,
            new
            {
                id = runId,
                org_id = orgId,
                project_id = command.ProjectId,
                manifest_id = manifestId,
                policy_revision_id = activePolicy?.PolicyRevisionId,
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
                collection_timeout = collectionTimeout,
                late_window_minutes = lateWindowMinutes,
                ref_kind = identity.RefKind,
                ref_name = identity.RefName,
                pr_number = identity.PrNumber,
                head_sha = identity.HeadSha,
                base_sha = identity.BaseSha,
                trigger_event = identity.TriggerEvent,
                is_fork = identity.IsFork,
                unverified_ref = identity.UnverifiedRef,
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

        // The run's release binding, in the same transaction as the run, its executions and its slots: a failure after
        // this point rolls the binding back with the run, and a caller that replayed its Idempotency-Key never reaches
        // here at all (the idempotency stage answers the stored response).
        if (bindReleaseId is { } boundReleaseId)
        {
            // The binding writes its own audit row and outbox event here, in this transaction: a run create that pinned a
            // release changed the same rows an explicit bind would, so the chain must say so either way.
            await RunReleaseBinding
                .BindAsync(
                    boundReleaseId,
                    runId,
                    orgId,
                    components,
                    db,
                    outbox,
                    audit,
                    AuditActorResolver.From(principals.Current),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await outbox.EnqueueAsync(
            RunCreatedTaskType,
            JsonSerializer.Serialize(new
            {
                run_id = runId,
                project_id = command.ProjectId,
                manifest_id = manifestId,
                // 05.2.2-T1.4: the identity block travels with the event (the same seven read-model members and the
                // unverified label), so 07/09 need no second lookup. The three pre-existing members are unchanged, so
                // an existing consumer keeps working.
                @ref = new
                {
                    kind = identity.RefKind,
                    name = identity.RefName,
                    pr_number = identity.PrNumber,
                    head_sha = identity.HeadSha,
                    base_sha = identity.BaseSha,
                    trigger_event = identity.TriggerEvent,
                    is_fork = identity.IsFork,
                },
                unverified_ref = identity.UnverifiedRef,
            }),
            "run",
            runId,
            $"orchestrator:run:{runId}:created",
            cancellationToken).ConfigureAwait(false);

        audit.Describe(new AuditObject("run", runId.ToString("D")));
        activity?.SetTag("evidgate.run_id", runId.ToString("D"));
        if (identity.RefKind is { } refKind)
        {
            // ref_kind only: ref_name (which can carry a person's name) and the shas are never logged or tagged
            // (05.2.2 §8 — the tag is bounded to three values, the values themselves are high-cardinality).
            activity?.SetTag("evidgate.run.ref_kind", refKind);
        }

        scopes.Require().AfterCommit(() =>
            EvidgateTelemetry.RecordRunCreated(identity.TriggerEvent, identity.IsFork, identity.UnverifiedRef));

        return Result.Success(new RunCreated(runId, "queued", identity.UnverifiedRef), 201);
    }

    /// <summary>
    /// The verified <c>ref</c>/<c>sha</c> claims of the caller's credential (05.2.2-T1.3): the <c>ci_token_issue</c>
    /// row the 02.4.1 exchange wrote for the presented <c>ci_producer</c> token, found by the run reference the minted
    /// token carries (<c>repository_id</c>, <c>run_id</c>, <c>run_attempt</c>). The exchange mints the token's
    /// <c>jti</c> as a fresh GUID and stores the GitHub OIDC <c>jti</c> separately, so that key is not a link between
    /// the token in hand and the row; the run reference is (owner decision D-S45-Q2). Only a principal that carries a
    /// <see cref="CiRunReference"/> can be a <c>ci_producer</c>, so every other caller short-circuits without a
    /// statement (00 §5.7 P3).
    /// <para>
    /// The Identity module owns <c>ci_token_issue</c>; this module reads it as raw SQL like the Evidence module reads
    /// <c>run</c> (the cross-module read precedent), so no Identity type or contract is involved. RLS already scopes
    /// the row to this tenant (<c>ci_token_issue_app_tenant</c>); <c>org_id</c> and <c>project_id</c> repeat the scope
    /// in the statement as defence in depth (00 §5.4). A row that is not there — purged after its replay window,
    /// another project's, or another run attempt's — means NOT verified: the caller's body values are stored and
    /// labelled <c>unverified_ref</c> rather than trusted. <c>ref</c>/<c>sha</c> are fixed per run attempt, so the
    /// newest row for the attempt is authoritative and the statement takes it with <c>limit 1</c>.
    /// </para>
    /// </summary>
    private async ValueTask<CiRefClaim?> ReadVerifiedClaimAsync(
        IRequestPrincipal? principal,
        Guid projectId,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        if (principal?.CiRun is not { } ciRun)
        {
            return null;
        }

        var row = await db.QuerySingleOrDefaultAsync<CiTokenIssueRow>(
            OrchestratorSql.CiTokenIssueClaim,
            new
            {
                org_id = orgId,
                project_id = projectId,
                repository_id = ciRun.RepositoryId,
                run_id = ciRun.RunId,
                run_attempt = ciRun.RunAttempt,
            },
            cancellationToken).ConfigureAwait(false);

        return row is null ? null : new CiRefClaim(row.Ref, row.Sha);
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
        IngestionLimits limits,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(manifest.Document);
        var root = document.RootElement;
        var planned = root.GetProperty("planned_executions");
        var shards = root.GetProperty("shard_plan").GetProperty("shards");

        // The counts are read from the stored document before any list is allocated for them: an over-cap
        // manifest costs one property read and creates nothing, however large the document is.
        var count = planned.GetArrayLength();
        if (count > limits.MaxManifestExecutions)
        {
            return (null, CapExceeded("manifest_executions", limits.MaxManifestExecutions, count));
        }

        var variants = root.GetProperty("matrix").GetProperty("variants").GetArrayLength();
        if (variants > limits.MaxManifestVariants)
        {
            return (null, CapExceeded("manifest_variants", limits.MaxManifestVariants, variants));
        }
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

    /// <summary>The 422 a manifest over one published cap earns: the cap name, its limit and the declared count.</summary>
    private static Error CapExceeded(string cap, long limit, long actual)
    {
        EvidgateTelemetry.RecordIngestCapExceeded(cap);

        return Error.Unprocessable(
            ApiErrorCode.CapExceeded,
            string.Create(CultureInfo.InvariantCulture, $"the manifest exceeds the {cap} limit"),
            [new ErrorDetail(cap, ApiErrorCode.CapExceeded, Limit: limit, Actual: actual)]);
    }

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

    /// <summary>
    /// One <c>ci_token_issue</c> row as <see cref="OrchestratorSql.CiTokenIssueClaim"/> returns it (Dapper maps the
    /// snake_case columns): the two verified claims 05.2.2-T1.3 reads. <c>ref</c> and <c>sha</c> are fixed per run
    /// attempt, so the statement returns the newest row of the attempt and the read is a single row or none.
    /// </summary>
    private sealed class CiTokenIssueRow
    {
        public string Ref { get; set; } = string.Empty;

        public string Sha { get; set; } = string.Empty;
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
