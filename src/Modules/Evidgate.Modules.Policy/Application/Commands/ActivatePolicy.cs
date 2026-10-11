using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Contracts;
using Evidgate.Modules.Policy.Domain;
using Mediator;

namespace Evidgate.Modules.Policy.Application.Commands;

/// <summary>
/// Activate a policy revision for a project (07.1.1 §5/§6, T4.1): the pointer a run created afterwards
/// pins (FR-07-2, ruling T93). Held by <c>release_approver</c> through <c>policy:write</c>, the same
/// permission the revision append requires, so a member who may write a revision may activate one.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Body">The raw request body, read under the route's 4 KiB cap (<c>PolicyEndpoints.MaxActivationBytes</c>) and parsed in the handler.</param>
internal sealed record ActivatePolicyCommand(Guid ProjectId, ReadOnlyMemory<byte> Body) : ICommand<Result<PolicyActivationDto>>;

/// <summary>
/// The activation command (07.1.1 §6): resolve the caller, hold the project scope, parse the body, run
/// the activation store, then describe the audit row. Everything the store writes commits in the caller's
/// tenant transaction, the audit row included, so a refused activation writes nothing (AC: exactly one
/// audit row per successful call, none per refusal).
/// <para>
/// The body is the route's 4 KiB request (<c>PolicyEndpoints.MaxActivationBytes</c>), and the handler parses
/// it with the append route's two edges: a member name or string that jsonb cannot carry (a lone surrogate
/// or U+0000) is a 422 before the walk touches it (<c>PolicyRevisionRules.HasUnstorableText</c>, 07.1.1-T4
/// F2a), and a body of many unknown members is answered with at most <see cref="MaxDetails"/> capped paths
/// (07.1.1-T4 F2b), so a hostile body cannot turn a small refusal into an unbounded envelope.
/// </para>
/// </summary>
[AuditedAction("policy.activate")]
[Permission(Permissions.Policy.Write)]
internal sealed class ActivatePolicyCommandHandler(
    PolicyActivationStore store,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<ActivatePolicyCommand, Result<PolicyActivationDto>>
{
    /// <summary>The only mode this story accepts; <c>enforcing</c> lands with 07.7.1 (07.1.1 §6).</summary>
    private const string Advisory = "advisory";

    /// <summary>At most this many details are echoed, so a body of many unknown members stays bounded (07.1.1-T4 F2b).</summary>
    private const int MaxDetails = 20;

    /// <summary>Longest member name echoed as a detail path; a longer one is cut (07.1.1-T4 F2b, the 200 of <c>PolicyBodyPaths.Of</c>).</summary>
    private const int MaxPathLength = 200;

    /// <summary>Runs the activation.</summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<Result<PolicyActivationDto>> Handle(ActivatePolicyCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(ActivatePolicyCommand));

        // A project-scoped service token must not activate a policy (no self-escalation): policy:write is
        // outside the service-role ceiling, so only a member reaches here; fail closed anyway.
        if (principals.Current?.UserId is not { } activatedBy)
        {
            return Result.Failure<PolicyActivationDto>(Error.Forbidden("policy:write is held by a member"));
        }

        if (PolicyScope.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<PolicyActivationDto>(mismatch);
        }

        if (Parse(command.Body) is var parsed && parsed.Request is null)
        {
            return Result.Failure<PolicyActivationDto>(parsed.Error!);
        }

        var activated = await store.ActivateAsync(command.ProjectId, parsed.Request.RevisionId, parsed.Request.Mode, activatedBy, cancellationToken)
            .ConfigureAwait(false);
        if (!activated.IsSuccess)
        {
            return Result.Failure<PolicyActivationDto>(activated.Error!);
        }

        var outcome = activated.Value;

        // One audit row per call, on both outcomes (C9): the revision id is the object, its hash the
        // after-hash, and the metadata carries what the row must tell apart (`changed`/`unchanged`, the
        // retired revision and the stored mode).
        audit.Describe(new AuditObject(
            "policy_activation",
            outcome.PolicyRevisionId.ToString("D"),
            AfterHash: Convert.FromHexString(outcome.Hash[CanonicalHash.Prefix.Length..]),
            Metadata: new Dictionary<string, string>
            {
                ["outcome"] = outcome.Changed ? "changed" : "unchanged",
                ["previous_id"] = outcome.PreviousPolicyRevisionId?.ToString("D") ?? string.Empty,
                ["mode"] = outcome.Mode,
            }));

        return Result.Success(new PolicyActivationDto(
            command.ProjectId,
            outcome.PolicyRevisionId,
            outcome.Hash,
            outcome.Mode,
            outcome.ActivatedBy,
            outcome.ActivatedAt,
            outcome.PreviousPolicyRevisionId,
            outcome.Changed));
    }

    /// <summary>
    /// Parses the body: <c>policy_revision_id</c> is required and must be a UUID; <c>mode</c> is optional
    /// and defaults to <c>advisory</c>. <c>enforcing</c> is refused 422 until 07.7.1 (07.1.1 §6). The body
    /// is closed against unknown members by the same reading the manifest routes use (00 §5.5 edge), a
    /// name or string jsonb cannot carry is refused 422 before the walk reads it (07.1.1-T4 F2a), and the
    /// unknown-member details are capped in number and path length (07.1.1-T4 F2b).
    /// </summary>
    private static (ActivatePolicyRequest? Request, Error? Error) Parse(ReadOnlyMemory<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return (null, Error.Invalid("the request body must be a JSON object"));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, Error.Invalid("the request body must be a JSON object"));
            }

            // The append route's guard (07.1.1-T4 F2a): GetString() and member.Name below throw on a lone
            // surrogate or U+0000, so the body is refused 422 here rather than escaping as a 500.
            if (PolicyRevisionRules.HasUnstorableText(root))
            {
                return (null, Error.Invalid(
                    "the request body carries a string that cannot be stored",
                    [new ErrorDetail(string.Empty, "invalid_value")]));
            }

            var details = new List<ErrorDetail>();
            Guid? revisionId = null;
            if (!root.TryGetProperty("policy_revision_id", out var revisionProperty)
                || revisionProperty.ValueKind != JsonValueKind.String
                || !Guid.TryParse(revisionProperty.GetString(), out var parsedRevision))
            {
                details.Add(new ErrorDetail("policy_revision_id", "required"));
            }
            else
            {
                revisionId = parsedRevision;
            }

            var mode = Advisory;
            if (root.TryGetProperty("mode", out var modeProperty))
            {
                if (modeProperty.ValueKind != JsonValueKind.String)
                {
                    details.Add(new ErrorDetail("mode", "invalid_value"));
                }
                else
                {
                    mode = modeProperty.GetString()!;
                    if (!string.Equals(mode, Advisory, StringComparison.Ordinal))
                    {
                        details.Add(new ErrorDetail(
                            "mode",
                            string.Equals(mode, "enforcing", StringComparison.Ordinal) ? "not_yet_available" : "invalid_value"));
                    }
                }
            }

            foreach (var member in root.EnumerateObject())
            {
                if (details.Count >= MaxDetails)
                {
                    // Cap reached: stop enumerating, so a body of many unknown members costs one bounded list.
                    break;
                }

                if (member.Name is not ("policy_revision_id" or "mode"))
                {
                    var name = member.Name;
                    details.Add(new ErrorDetail(
                        name.Length > MaxPathLength ? name[..MaxPathLength] : name,
                        "unknown_member"));
                }
            }

            return details.Count > 0
                ? (null, Error.Invalid("the activation request is invalid", details))
                : (new ActivatePolicyRequest(revisionId!.Value, mode), null);
        }
    }

    private sealed record ActivatePolicyRequest(Guid RevisionId, string Mode);
}
