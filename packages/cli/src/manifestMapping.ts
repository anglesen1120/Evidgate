// Maps the tests a Playwright list reports to the members of one catalog suite revision.
// The mapping is all-or-nothing: one unmapped or ambiguous test refuses the whole plan, so a
// run can never claim a suite revision while some of its tests are silently absent.
import type { SuiteRevisionExecutionPlanMember } from '@evidgate/sdk';

import { CliError } from './errors.js';
import { redact } from './redact.js';

/** Tests accepted in one mapping. */
export const MAX_TESTS = 10_000;
/** Members accepted from one execution-plan read. */
export const MAX_MEMBERS = 10_000;
/** Offending tests named in a refusal; the rest are counted, not printed. */
const MAX_DETAILS = 20;
/** File and each title segment are clipped to this many characters in a refusal. */
const MAX_DESCRIBED_CHARS = 200;

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

/**
 * One member of a suite revision as the execution plan lists it; the SDK's own type, so the
 * route's shape is declared once. The catalogue names this a test revision per case.
 */
export type PlanMember = SuiteRevisionExecutionPlanMember;

/** The execution plan of one suite revision. */
export interface ExecutionPlan {
  readonly suite_revision_id: string;
  readonly members: readonly PlanMember[];
}

export interface MappingEntry<T extends EnumeratedTest = EnumeratedTest> {
  readonly test: T;
  readonly caseRevisionId: string;
}

export interface MappingResult<T extends EnumeratedTest = EnumeratedTest> {
  readonly suiteRevisionId: string;
  /** One entry per input test, in input order, carrying the caller's own test type. */
  readonly entries: readonly MappingEntry<T>[];
}

type ProblemKind = 'manifest_test_unmapped' | 'manifest_test_ambiguous';

type Lookup =
  { readonly kind: 'mapped'; readonly member: PlanMember } | { readonly kind: ProblemKind };

/**
 * Resolves every test against the plan, or refuses with the first problem class it met and
 * up to `MAX_DETAILS` offending tests. Nothing partial is ever returned: a caller that gets a
 * result can treat every entry as mapped.
 */
export function mapTestsToMembers<T extends EnumeratedTest>(
  tests: readonly T[],
  plan: ExecutionPlan,
): MappingResult<T> {
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

  const entries: MappingEntry<T>[] = [];
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
export function memberKey(caseId: string, variantHash: string): string {
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
  const named = problems.slice(0, MAX_DETAILS).map((problem) => describeTest(problem.test));
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

/**
 * A test's spec file and title path, bounded per segment so a crafted report with megabyte
 * titles cannot grow an error. Each segment is redacted before it is clipped, and the assembled
 * text is redacted once more: a secret assembled across the file/title separators is replaced
 * while complete, so no prefix of it can survive a clip. The readable per-segment shape is kept
 * whenever the joined text adds nothing (the common case), and the joined text is clipped whole
 * only when such a cross-segment secret was found.
 */
export function describeTest(test: EnumeratedTest): string {
  const titleParts = test.titlePath.length === 0 ? ['(no title path)'] : test.titlePath;
  const redactedFile = redact(test.file);
  const redactedTitles = titleParts.map(redact);
  const assembled = `${redactedFile}: ${redactedTitles.join(' > ')}`;
  const safe = redact(assembled);
  if (safe !== assembled) {
    return clip(safe);
  }
  const title = redactedTitles.map(clip).join(' > ');
  return `${clip(redactedFile)}: ${title}`;
}

function clip(value: string): string {
  return value.length <= MAX_DESCRIBED_CHARS ? value : `${value.slice(0, MAX_DESCRIBED_CHARS)}…`;
}

function inputTooLarge(message: string): CliError {
  return new CliError(message, { code: 'manifest_input_too_large' });
}

/**
 * Refuses two tests that resolve to the same case and variant. A report may repeat a test
 * through `repeatEach` or a duplicated spec, and either way one manifest execution would be
 * claimed by two results, so the whole report is refused. Bounded like every other refusal.
 */
export function assertDistinctTests(tests: readonly EnumeratedTest[]): void {
  const groups = new Map<string, EnumeratedTest[]>();
  for (const test of tests) {
    if (test.identityConflict === true || test.caseId === undefined) {
      continue;
    }
    const key = memberKey(test.caseId, test.variantHash);
    const group = groups.get(key);
    if (group === undefined) {
      groups.set(key, [test]);
    } else {
      group.push(test);
    }
  }
  const repeated = [...groups.values()].filter((group) => group.length > 1).flat();
  if (repeated.length === 0) {
    return;
  }
  // Describe only the named offenders: a crafted report can repeat a megabyte title thousands
  // of times, so the redaction work is bounded before it starts, not after it is thrown away.
  const described = repeated.slice(0, MAX_DETAILS).map(describeTest);
  const details = boundedDetails(described, repeated.length);
  throw new CliError(`test ${details[0] ?? '(unnamed)'} repeats a case and variant in the report`, {
    code: 'manifest_test_duplicate',
    hint: 'keep one test per case and variant; do not repeat a case with repeatEach',
    details,
  });
}

/**
 * Refuses a plan member no local test exercises. A manifest that omits a member would let a run
 * claim the revision while part of it never runs, so the plan must be fully covered.
 */
export function assertMembersExercised(
  plan: ExecutionPlan,
  entries: readonly MappingEntry[],
): void {
  const covered = new Set(
    entries.map((entry) => memberKey(entry.test.caseId ?? '', entry.test.variantHash)),
  );
  const missing = plan.members.filter(
    (member) => !covered.has(memberKey(member.case_id, member.variant_hash)),
  );
  if (missing.length === 0) {
    return;
  }
  const details = boundedDetails(
    missing
      .slice(0, MAX_DETAILS)
      .map((member) => `case=${member.case_id} variant=${member.variant_hash}`),
    missing.length,
  );
  throw new CliError(
    `suite revision ${plan.suite_revision_id} lists ${missing.length} member(s) the report does not exercise`,
    {
      code: 'manifest_plan_member_unexercised',
      hint: 'add the missing case and variant to the Playwright project, or build for another revision',
      details,
    },
  );
}

/** At most `MAX_DETAILS` named items, then one line counting the rest (`total` if given). */
function boundedDetails(named: readonly string[], total = named.length): string[] {
  const shown = named.slice(0, MAX_DETAILS);
  const remaining = total - shown.length;
  return remaining > 0 ? [...shown, `(${remaining} more)`] : shown;
}
