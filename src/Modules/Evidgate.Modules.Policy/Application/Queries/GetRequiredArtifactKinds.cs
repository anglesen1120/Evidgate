using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.Modules.Policy.Contracts;

namespace Evidgate.Modules.Policy.Application.Queries;

/// <summary>
/// Reads the artifact kinds one policy revision requires a run to produce (05.3.2-T2, 00 §7): the in-module
/// reader behind <see cref="IPolicyContracts.GetRequiredArtifactKindsAsync"/>. The endpoint layer never calls it
/// (the public read surface is the policy-revision routes), exactly like <see cref="GetActivePolicyQuery"/>.
/// <para>
/// One lock-free statement inside the caller's tenant transaction: the revision is matched by its id and by the
/// ambient scope's org on top of RLS, and its body's <c>required_artifacts</c> array
/// (<c>contracts/schemas/policy-revision.v1.json</c>) is expanded with <c>jsonb_array_elements_text</c> only when
/// <c>jsonb_typeof</c> says the value is an array, so a body without the field — or with a non-array value —
/// reads no row instead of raising. <c>distinct</c> de-duplicates inside the statement.
/// </para>
/// <para>
/// An unknown or invisible revision id reads no row and answers an empty list: "nothing required" is the answer,
/// not an error, the same shape as <see cref="GetActivePolicyQuery"/> answering null for "no active policy".
/// </para>
/// </summary>
internal sealed class GetRequiredArtifactKindsQuery(ITenantDb db, ITenantScopeAccessor scopes)
{
    /// <summary>The revision's required artifact kinds, ordinal and de-duplicated; empty when it has none.</summary>
    /// <param name="policyRevisionId">The revision to read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<IReadOnlyList<string>> GetAsync(Guid policyRevisionId, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(GetRequiredArtifactKindsQuery));

        var orgId = scopes.Require().OrgId;

        var kinds = await db.QueryAsync<string>(
            GetRequiredArtifactKindsSql.RequiredArtifactKinds,
            new { org_id = orgId, policy_revision_id = policyRevisionId },
            cancellationToken).ConfigureAwait(false);

        // The schema allows ASCII kinds only, but the body column carries no kind check of its own: order
        // ordinally here so the answer is the contract's ordinal order whatever text a stored body holds.
        return [.. kinds.Order(StringComparer.Ordinal)];
    }
}

/// <summary>The statement of <see cref="GetRequiredArtifactKindsQuery"/>; it runs in the caller's tenant transaction.</summary>
internal static class GetRequiredArtifactKindsSql
{
    /// <summary>
    /// The revision's <c>required_artifacts</c> elements, de-duplicated, one text per row. The array is expanded
    /// only when <c>jsonb_typeof</c> reports an array — an absent field reads JSON null and a body that wrote a
    /// non-array (an object, a scalar) would otherwise raise <c>22023</c> instead of answering "nothing required".
    /// The explicit <c>org_id</c> predicate is the module's own tenant match on top of RLS (C3): an unknown or other
    /// tenant's revision reads no row, and the read takes no lock and writes nothing (C1).
    /// </summary>
    internal const string RequiredArtifactKinds = """
        select distinct k.value as kind
          from public.policy_revision pr
          cross join lateral jsonb_array_elements_text(
                 case jsonb_typeof(pr.body -> 'required_artifacts')
                      when 'array' then pr.body -> 'required_artifacts'
                      else '[]'::jsonb
                 end) as k(value)
         where pr.id = @policy_revision_id
           and pr.org_id = @org_id
        """;
}
