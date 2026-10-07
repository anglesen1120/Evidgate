using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Contracts;
using Evidgate.Modules.Policy.Domain;
using Mediator;

namespace Evidgate.Modules.Policy.Application.Commands;

/// <summary>Append a policy revision (<c>POST /v1/projects/{p}/policies</c>, 07.1.1 §5).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Body">The raw UTF-8 body (≤ 64 KiB), parsed and schema-checked by the handler after authorization.</param>
/// <param name="IfMatch">The caller's cached head revision id, or null for no precondition (00 §9.1).</param>
internal sealed record CreatePolicyRevisionCommand(
    Guid ProjectId,
    [property: JsonIgnore] ReadOnlyMemory<byte> Body,
    string? IfMatch)
    : ICommand<Result<PolicyRevisionDto>>;

/// <summary>
/// Handler of <see cref="CreatePolicyRevisionCommand"/>. The body is parsed, validated against
/// <c>policy-revision.v1</c> and checked against the domain rules before the store is called, so the store's own
/// guards are unreachable from HTTP. A body whose canonical form is already on file returns the stored revision
/// with 200 (AC1); new content gets the next number with 201. A stale <c>If-Match</c> is 412 and writes nothing
/// (AC5), decided under the store's project row lock.
/// </summary>
[AuditedAction("policy_revision.create")]
[Permission(Permissions.Policy.Write)]
internal sealed class CreatePolicyRevisionCommandHandler(
    PolicyRevisionStore store,
    IPolicyHasher hasher,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    Lazy<ContractSchemaSet> schemas,
    IAuditTrail audit)
    : ICommandHandler<CreatePolicyRevisionCommand, Result<PolicyRevisionDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PolicyRevisionDto>> Handle(CreatePolicyRevisionCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(CreatePolicyRevisionCommand));

        if (PolicyScope.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<PolicyRevisionDto>(mismatch);
        }

        // policy:write is outside the service-role ceiling, so only a member reaches this handler; fail closed anyway.
        if (principals.Current?.UserId is not { } createdBy)
        {
            return Result.Failure<PolicyRevisionDto>(Error.Forbidden("policy:write is held by a member"));
        }

        if (Validate(command.Body, schemas.Value, hasher, out var body, out var element) is { } bodyError)
        {
            return Result.Failure<PolicyRevisionDto>(bodyError);
        }

        Guid? expectedHeadId = null;
        if (!string.IsNullOrEmpty(command.IfMatch))
        {
            if (!PolicyIfMatch.TryParse(command.IfMatch, out var expected))
            {
                return Result.Failure<PolicyRevisionDto>(PolicyErrors.IfMatchInvalid);
            }

            expectedHeadId = expected;
        }

        var appended = await store.AppendAsync(command.ProjectId, body, createdBy, cancellationToken, expectedHeadId)
            .ConfigureAwait(false);
        if (!appended.IsSuccess)
        {
            return Result.Failure<PolicyRevisionDto>(appended.Error!);
        }

        var row = appended.Value;
        audit.Describe(new AuditObject("policy_revision", row.Id.ToString("D")));

        return Result.Success(
            new PolicyRevisionDto(
                row.Id,
                command.ProjectId,
                row.RevisionNo,
                row.Hash,
                row.State,
                element,
                row.CreatedBy,
                row.CreatedAt,
                row.Version),
            row.Created ? 201 : 200);
    }

    /// <summary>
    /// Null when the body is a storable JSON object the schema accepts and the domain rules admit, else the 422.
    /// The order is the route's: JSON, then the schema (an unknown member is its own code, AC3), then the rules the
    /// store would otherwise enforce with an exception.
    /// </summary>
    private static Error? Validate(
        ReadOnlyMemory<byte> bytes,
        ContractSchemaSet schemas,
        IPolicyHasher hasher,
        out string body,
        out JsonElement element)
    {
        body = string.Empty;
        element = default;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return PolicyErrors.PolicyInvalid(
                "the body is not JSON",
                [new ErrorDetail(string.Empty, ContractValidationError.InvalidJsonReason)]);
        }

        using (document)
        {
            // Before the schema engine, which throws on an unpaired surrogate or U+0000.
            if (PolicyRevisionRules.HasUnstorableText(document.RootElement))
            {
                return PolicyErrors.PolicyInvalid(
                    "the body carries a string that cannot be stored",
                    [new ErrorDetail(string.Empty, "invalid_value")]);
            }

            var verdict = schemas.Validate(ContractSchemaKeys.PolicyRevision, document.RootElement);
            if (!verdict.IsValid)
            {
                var unknown = verdict.Errors.Where(PolicyBodyPaths.IsUnknownField).ToArray();
                return unknown.Length > 0
                    ? PolicyErrors.UnknownField(
                        "the body carries a field policy-revision.v1 does not declare",
                        [.. unknown.Select(error => new ErrorDetail(PolicyBodyPaths.Of(error), error.Reason, error.Index))])
                    : PolicyErrors.PolicyInvalid(
                        "the body does not match policy-revision.v1",
                        [.. verdict.Errors.Select(error => new ErrorDetail(PolicyBodyPaths.Of(error), error.Reason, error.Index))]);
            }

            element = document.RootElement.Clone();
        }

        body = Encoding.UTF8.GetString(bytes.Span);

        switch (PolicyRevisionRules.ClassifyNumbers(body))
        {
            case PolicyNumberOutcome.InexactNumber:
                return PolicyErrors.PolicyInvalid(
                    "the body carries a number that is not exactly representable as an IEEE 754 double",
                    [new ErrorDetail(string.Empty, PolicyRevisionCorruptionReasons.InexactNumber)]);
            case PolicyNumberOutcome.NotCanonicalisable:
                return PolicyErrors.PolicyInvalid(
                    "the body cannot be canonicalised",
                    [new ErrorDetail(string.Empty, PolicyRevisionCorruptionReasons.NotCanonicalisable)]);
        }

        try
        {
            hasher.Hash(body);
        }
        catch (JsonCanonicalizationException)
        {
            return PolicyErrors.PolicyInvalid(
                "the body cannot be canonicalised",
                [new ErrorDetail(string.Empty, PolicyRevisionCorruptionReasons.NotCanonicalisable)]);
        }

        return null;
    }
}
