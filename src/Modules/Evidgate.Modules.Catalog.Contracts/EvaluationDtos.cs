namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>
/// The catalogue facts an evaluation reads about the suite revision a run pinned: the suite's key (the evaluator
/// reports results per suite) and whether that revision declares no case at all, which is how a suite that planned
/// nothing is told apart from one whose members were lost.
/// </summary>
/// <param name="SuiteKey">The <c>suite.slug</c> of the revision's suite.</param>
/// <param name="DeclaredEmpty">True when the revision has no <c>suite_member</c> row.</param>
public sealed record EvaluationSuite(string SuiteKey, bool DeclaredEmpty);

/// <summary>
/// The case behind one test revision, as an evaluation needs it: the id the result row names and the display name it
/// reports under. A revision is the identity an attempt carries, so this is the map from a planned revision to its case.
/// </summary>
/// <param name="CaseId">The <c>test_case.id</c>.</param>
/// <param name="CaseKey">The <c>test_case.name</c>.</param>
public sealed record EvaluationCaseIdentity(Guid CaseId, string CaseKey);
