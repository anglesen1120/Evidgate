// Maps the tests a Playwright list reports to the members of one catalog suite revision.
// The mapping is all-or-nothing: one unmapped or ambiguous test refuses the whole plan, so a
// run can never claim a suite revision while some of its tests are silently absent.
import { CliError } from './errors.js';

/** Tests accepted in one mapping. */
export const MAX_TESTS = 10_000;
/** Members accepted from one execution-plan read. */
export const MAX_MEMBERS = 10_000;
/** Offending tests named in a refusal; the rest are counted, not printed. */
const MAX_DETAILS = 20;

/** One test as the list report describes it, with its resolved identity. */
export interface EnumeratedTest {
  /** Project-relative POSIX spec file. */
  readonly file: string;
  readonly titlePath: readonly string[];
  /** Canonical lowercase case id from the annotation; absent when none or a conflict. */
  readonly caseId?: string;
  readonly variantHash: string;
  /** Set by the parser when two or more distinct valid `evidgate.id` annotations exist. */
  readonly identityConflict?: boolean;
}

// interim local type: replace by the SDK type when the execution-plan route lands
/** One member of a suite revision as the execution plan lists it. */
export interface PlanMember {
  readonly case_id: string;
  readonly variant_hash: string;
  readonly case_revision_id: string;
}

/** The execution plan of one suite revision. */
export interface ExecutionPlan {
  readonly suite_revision_id: string;
  readonly members: readonly PlanMember[];
}

/**
 * The server-independent port the mapping reads a plan through. A paged execution-plan route
 * will need a 20-page cap; until then one read returns every member, so no page cap is
 * exported and none is applied.
 */
export interface ExecutionPlanPort {
  read(suiteRevisionId: string): Promise<ExecutionPlan>;
}

export interface MappingEntry {
  readonly test: EnumeratedTest;
  readonly caseRevisionId: string;
}

export interface MappingResult {
  readonly suiteRevisionId: string;
  /** One entry per input test, in input order. */
  readonly entries: readonly MappingEntry[];
}

type ProblemKind = 'manifest_test_unmapped' | 'manifest_test_ambiguous';

type Lookup =
  { readonly kind: 'mapped'; readonly member: PlanMember } | { readonly kind: ProblemKind };

/**
 * Resolves every test against the plan, or refuses with the first problem class it met and
 * up to `MAX_DETAILS` offending tests. Nothing partial is ever returned: a caller that gets a
 * result can treat every entry as mapped.
 */
export function mapTestsToMembers(
  tests: readonly EnumeratedTest[],
  plan: ExecutionPlan,
): MappingResult {
  if (tests.length > MAX_TESTS) {
    throw inputTooLarge(`the report lists ${tests.length} tests, over the ${MAX_TESTS} accepted`);
  }
  if (plan.members.length > MAX_MEMBERS) {
    throw inputTooLarge(
      `the suite revision lists ${plan.members.length} members, over the ${MAX_MEMBERS} accepted`,
    );
  }

  const byKey = new Map<string, PlanMember[]>();
  for (const member of plan.members) {
    const key = memberKey(member.case_id, member.variant_hash);
    const members = byKey.get(key);
    if (members === undefined) {
      byKey.set(key, [member]);
    } else {
      members.push(member);
    }
  }

  const entries: MappingEntry[] = [];
  const problems: Array<{ readonly kind: ProblemKind; readonly test: EnumeratedTest }> = [];
  for (const test of tests) {
    const lookup = lookupMember(test, byKey);
    if (lookup.kind === 'mapped') {
      entries.push({ test, caseRevisionId: lookup.member.case_revision_id });
    } else {
      problems.push({ kind: lookup.kind, test });
    }
  }

  const first = problems[0];
  if (first !== undefined) {
    throw refusal(
      first.kind,
      problems.filter((problem) => problem.kind === first.kind),
      plan,
    );
  }
  return { suiteRevisionId: plan.suite_revision_id, entries };
}

/** The exact pair a member and a test must agree on; the NUL separator cannot occur in either. */
function memberKey(caseId: string, variantHash: string): string {
  return `${caseId}\u0000${variantHash}`;
}

function lookupMember(test: EnumeratedTest, byKey: ReadonlyMap<string, PlanMember[]>): Lookup {
  // A conflicting annotation has no single identity, so no member can be the right one.
  if (test.identityConflict === true) {
    return { kind: 'manifest_test_ambiguous' };
  }
  const caseId = test.caseId;
  if (caseId === undefined) {
    return { kind: 'manifest_test_unmapped' };
  }
  const members = byKey.get(memberKey(caseId, test.variantHash)) ?? [];
  const [member] = members;
  if (member === undefined) {
    return { kind: 'manifest_test_unmapped' };
  }
  return members.length > 1 ? { kind: 'manifest_test_ambiguous' } : { kind: 'mapped', member };
}

/**
 * The refusal for one problem class. Only the spec file and title path identify a test: the
 * variant params never reach an error, because their values may be secrets.
 */
function refusal(
  kind: ProblemKind,
  problems: ReadonlyArray<{ readonly test: EnumeratedTest }>,
  plan: ExecutionPlan,
): CliError {
  const named = problems.slice(0, MAX_DETAILS).map((problem) => describe(problem.test));
  const subject =
    kind === 'manifest_test_unmapped'
      ? 'has no catalog member in'
      : 'matches more than one catalog member in';
  const hint =
    kind === 'manifest_test_unmapped'
      ? 'add an evidgate.id annotation naming the case, then add that case and variant to the suite revision'
      : 'keep one member per case and variant, and one evidgate.id annotation per test';
  const namedTest = named[0] ?? '(unnamed)';
  const message = `test ${namedTest} ${subject} suite revision ${plan.suite_revision_id}`;
  return new CliError(message, {
    code: kind,
    hint,
    ...(named.length === 0 ? {} : { details: named }),
  });
}

function describe(test: EnumeratedTest): string {
  const title = test.titlePath.length === 0 ? '(no title path)' : test.titlePath.join(' > ');
  return `${test.file}: ${title}`;
}

function inputTooLarge(message: string): CliError {
  return new CliError(message, { code: 'manifest_input_too_large' });
}
