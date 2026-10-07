using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Domain;
using Mediator;

namespace Evidgate.Modules.Policy.Application.Queries;

/// <summary>Read one policy revision (<c>GET /v1/policies/{id}</c>, 07.1.1 §5).</summary>
/// <param name="Id">The revision.</param>
internal sealed record GetPolicyRevisionQuery(Guid Id) : IQuery<Result<PolicyRevisionView>>;

/// <summary>
/// The stored revision as the read surface returns it (07.1.1 §5: <c>state</c>, <c>hash</c> and <c>body</c> included).
/// <c>Body</c> is the text PostgreSQL returns for the <c>jsonb</c> column, so its key order and whitespace are the
/// database's, not the writer's — the handler re-canonicalises it before comparing addresses.
/// </summary>
internal sealed record PolicyRevisionView(
    Guid Id,
    Guid ProjectId,
    int RevisionNo,
    string Hash,
    string Body,
    string State,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    int Version);

/// <summary>
/// Handler of <see cref="GetPolicyRevisionQuery"/>: one statement, then the corruption check of 07.1.1-T2.2 — the
/// stored <c>body</c> is re-canonicalised and re-hashed and must equal the stored <c>hash</c> column, so a row
/// mutated outside the append-only path (the app role holds no <c>UPDATE</c> grant) is refused on read instead of
/// being served under the wrong content address. Another tenant's revision is 404 (RLS hides it), never 403.
/// </summary>
[Permission(Permissions.Policy.Read)]
internal sealed class GetPolicyRevisionQueryHandler(ITenantDb db, IPolicyHasher hasher)
    : IQueryHandler<GetPolicyRevisionQuery, Result<PolicyRevisionView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PolicyRevisionView>> Handle(GetPolicyRevisionQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(GetPolicyRevisionQuery));

        var row = await db.QuerySingleOrDefaultAsync<PolicyRevisionView>(
            PolicyRevisionReadSql.ById,
            new { id = query.Id },
            cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            return Result.Failure<PolicyRevisionView>(PolicyRevisionReadErrors.RevisionNotFound);
        }

        Verify(row, hasher);

        return Result.Success(row);
    }

    /// <summary>
    /// Recomputes the content address of the stored body and refuses a row that no longer matches it (07.1.1-T2.2).
    /// A body that cannot be canonicalised, or that carries a number only a double can round, is corruption too, so
    /// the failure is reported as <see cref="PolicyRevisionCorruptedException"/> with a fixed reason rather than
    /// escaping as a different exception type. The cause is never chained: its message carries body content.
    /// </summary>
    private static void Verify(PolicyRevisionView row, IPolicyHasher hasher)
    {
        // Checked before the hash compare: a digit-tampered body would otherwise surface as a hash mismatch. A body
        // that is not well-formed JSON (malformed, or nested past the canonicalizer's depth cap) is not an inexact
        // number, so it is reported as not-canonicalisable — the same reason the hasher's own refusal carries.
        switch (PolicyRevisionRules.ClassifyNumbers(row.Body))
        {
            case PolicyNumberOutcome.InexactNumber:
                throw new PolicyRevisionCorruptedException(row.Id, row.Hash, null, PolicyRevisionCorruptionReasons.InexactNumber);
            case PolicyNumberOutcome.NotCanonicalisable:
                throw new PolicyRevisionCorruptedException(row.Id, row.Hash, null, PolicyRevisionCorruptionReasons.NotCanonicalisable);
        }

        string recomputed;
        try
        {
            recomputed = hasher.Hash(row.Body);
        }
        catch (JsonCanonicalizationException)
        {
            throw new PolicyRevisionCorruptedException(row.Id, row.Hash, null, PolicyRevisionCorruptionReasons.NotCanonicalisable);
        }

        if (!string.Equals(row.Hash, recomputed, StringComparison.Ordinal))
        {
            throw new PolicyRevisionCorruptedException(row.Id, row.Hash, recomputed, PolicyRevisionCorruptionReasons.HashMismatch);
        }
    }
}

/// <summary>Errors of the policy-revision read surface (07.1.1 §5, 00 §9.2).</summary>
internal static class PolicyRevisionReadErrors
{
    /// <summary>404: no revision with that id is visible to this tenant (another tenant's id included, 00 §9.2).</summary>
    internal static Error RevisionNotFound => Error.NotFound("no policy revision with that id is visible to this tenant");
}

/// <summary>The read statements of the policy-revision query; every one runs in the request's tenant transaction.</summary>
internal static class PolicyRevisionReadSql
{
    /// <summary>One revision by id; RLS scopes it to the request's tenant (00 §5.4.2), so another tenant reads no row.</summary>
    internal const string ById = """
        select id, project_id, revision_no, hash, body::text as body, state, created_by, created_at, version
          from public.policy_revision
         where id = @id
        """;
}
