namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>
/// The catalog surface other modules call in-process: run creation locks a suite revision and reads its
/// isolation mode, the blocked-attempt resolver walks dependencies, and the evaluator reads the suite facts and the case
/// behind each planned revision. Must run inside the caller's tenant transaction.
/// </summary>
public interface ICatalogContracts
{
    /// <summary>
    /// Marks the revision <c>published</c> — the one-way flip a run makes when it references the revision (00 §8.10).
    /// Idempotent; returns false when no such revision is visible to the tenant.
    /// </summary>
    /// <param name="suiteRevisionId">The revision.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<bool> PublishSuiteRevisionAsync(Guid suiteRevisionId, CancellationToken cancellationToken);

    /// <summary>True when <paramref name="suiteRevisionId"/> is a revision of a suite of <paramref name="projectId"/> visible to the tenant (05.1.1-T2.2).</summary>
    /// <param name="projectId">The project.</param>
    /// <param name="suiteRevisionId">The revision.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<bool> SuiteRevisionExistsAsync(Guid projectId, Guid suiteRevisionId, CancellationToken cancellationToken);

    /// <summary>
    /// Which of <paramref name="caseRevisionIds"/> are test revisions of cases of <paramref name="projectId"/> (05.1.1-T2.2): one
    /// statement however many ids; ids that are unknown, or belong to another project or tenant, are simply absent from the answer.
    /// </summary>
    /// <param name="projectId">The project.</param>
    /// <param name="caseRevisionIds">The ids to look up.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlySet<Guid>> FindCaseRevisionsAsync(Guid projectId, IReadOnlyCollection<Guid> caseRevisionIds, CancellationToken cancellationToken);

    /// <summary>
    /// The case each of <paramref name="caseRevisionIds"/> belongs to (05.2.1-T1.3): <c>case_revision_id → case_id</c> for the
    /// revisions of cases of <paramref name="projectId"/> visible to the tenant. One bounded statement however many ids; ids
    /// that are unknown, or belong to another project or tenant, are simply absent from the answer.
    /// </summary>
    /// <param name="projectId">The project.</param>
    /// <param name="caseRevisionIds">The ids to look up.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyDictionary<Guid, Guid>> GetCaseIdsAsync(Guid projectId, IReadOnlyCollection<Guid> caseRevisionIds, CancellationToken cancellationToken);

    /// <summary>
    /// The <c>variant_hash</c> of each planned execution's parameters (05.2.1-T1.3), index-aligned with
    /// <paramref name="variantParams"/>: the same <c>sha256:</c> the suite-member path stores for that variant, or null when
    /// the parameters are not a variant object or the normaliser refuses them (a secret-shaped key or value, a nested
    /// object). Pure: no I/O and no tenant scope.
    /// </summary>
    /// <param name="variantParams">The <c>variant.params</c> object of each planned execution.</param>
    IReadOnlyList<string?> NormalizeVariantHashes(IReadOnlyList<System.Text.Json.JsonElement> variantParams);

    /// <summary>
    /// The <c>data_isolation</c> the revision's environment requirements declare (<c>isolated</c> when omitted), or null
    /// when the revision does not exist for this tenant. The single isolation source (00 §15).
    /// </summary>
    /// <param name="suiteRevisionId">The revision.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<string?> GetDataIsolationAsync(Guid suiteRevisionId, CancellationToken cancellationToken);

    /// <summary>
    /// Every member of <paramref name="suiteRevisionId"/> that depends, directly or transitively, on the member
    /// <c>(caseRevisionId, variantHash)</c> through <c>case_dependency</c> (05.3.3-T2.2): the parent is the setup, the child the
    /// dependant. One statement however deep the graph (a <c>UNION</c> walk that terminates on its own even over a cycle, which
    /// 03.2.1-T3 refuses at write time); the root is never part of the answer; an unknown revision or member, or another tenant's, is an
    /// empty answer. Bounded by the revision's member count.
    /// </summary>
    /// <param name="suiteRevisionId">The suite revision the run executes.</param>
    /// <param name="caseRevisionId">The failed member's test revision.</param>
    /// <param name="variantHash">The failed member's variant hash.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<DependantMember>> GetDependantsAsync(Guid suiteRevisionId, Guid caseRevisionId, string variantHash, CancellationToken cancellationToken);

    /// <summary>
    /// The suite facts an evaluation of one run reads: the slug of the suite that owns
    /// <paramref name="suiteRevisionId"/> and whether that revision declares no member at all. One statement; null when
    /// the revision is unknown or of another tenant.
    /// </summary>
    /// <param name="orgId">The caller's trusted tenant id, so the read is tenant-exact beyond row level security.</param>
    /// <param name="suiteRevisionId">The suite revision the run pinned.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<EvaluationSuite?> GetEvaluationSuiteAsync(Guid orgId, Guid suiteRevisionId, CancellationToken cancellationToken);

    /// <summary>
    /// The case each of <paramref name="caseRevisionIds"/> belongs to, with its display name: one bounded
    /// statement however many ids. Ids that are unknown or of another tenant are simply absent from the answer, and an
    /// empty batch makes no round trip.
    /// </summary>
    /// <param name="orgId">The caller's trusted tenant id, so the read is tenant-exact beyond row level security.</param>
    /// <param name="caseRevisionIds">The revisions the run's planned executions name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyDictionary<Guid, EvaluationCaseIdentity>> GetEvaluationCasesAsync(Guid orgId, IReadOnlyCollection<Guid> caseRevisionIds, CancellationToken cancellationToken);

    /// <summary>
    /// Which of <paramref name="caseIds"/> are <c>archived</c> cases of <paramref name="projectId"/>: the only cases a
    /// projection over linked ids has to exclude to count a link as live, answered for a caller that may not read
    /// <c>test_case</c> itself. One statement however many ids, served by the case primary key; an id that is unknown
    /// or of another project or tenant is simply absent, and an empty batch makes no round trip.
    /// </summary>
    /// <param name="orgId">The caller's trusted tenant id, so the read is tenant-exact beyond row level security.</param>
    /// <param name="projectId">The project the cases must belong to.</param>
    /// <param name="caseIds">The <c>test_case.id</c> values to test for archived status.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<Guid>> GetArchivedCaseIdsAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyCollection<Guid> caseIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// The <c>status</c> of each of <paramref name="caseIds"/> that is a case of <paramref name="projectId"/> visible to
    /// the tenant: one bounded statement however many ids, and an empty batch makes no round trip. Ids that are
    /// unknown, or belong to another project or tenant, are simply absent — so a caller that must refuse a link to an
    /// unknown case and to another project's case in the same way cannot tell them apart, which is the point.
    /// </summary>
    /// <param name="orgId">The caller's trusted tenant id, so the read is tenant-exact beyond row level security.</param>
    /// <param name="projectId">The project the cases must belong to.</param>
    /// <param name="caseIds">The <c>test_case.id</c> values to look up.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyDictionary<Guid, string>> GetCaseStatusesAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyCollection<Guid> caseIds,
        CancellationToken cancellationToken);
}
