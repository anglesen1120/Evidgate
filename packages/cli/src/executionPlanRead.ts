// Reads one suite revision's execution plan as banner-paged cursors without trusting it: each
// page is validated before its members are kept, a repeated or empty cursor refuses the walk, and
// the page and member caps keep a crafted answer from growing the walk without bound.
import type { SuiteRevisionExecutionPlanMember } from '@evidgate/sdk';

import { isCanonicalCaseId } from './caseId.js';
import { CliError } from './errors.js';
import { MAX_MEMBERS } from './manifestMapping.js';

/** The route's own maximum page size (openapi `ExecutionPlanLimit`: default 200, at most 500). */
export const planPageLimit = 500;
/** Pages one walk accepts. Beyond this the plan is not a suite revision a person assembled. */
const maxPlanPages = 20;

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const VARIANT_HASH = /^sha256:[0-9a-f]{64}$/;

/** Fetches one page; `cursor` is absent on the first. Non-2xx mapping stays with the caller. */
export type PlanPageFetch = (cursor: string | undefined) => Promise<unknown>;

interface PlanPage {
  readonly members: readonly SuiteRevisionExecutionPlanMember[];
  readonly next: string | null;
}

/**
 * Walks every page and returns the members in plan order. An empty plan is refused: a manifest
 * with no execution would claim a revision while running nothing.
 */
export async function readExecutionPlan(
  suiteRevisionId: string,
  fetchPage: PlanPageFetch,
): Promise<readonly SuiteRevisionExecutionPlanMember[]> {
  const members: SuiteRevisionExecutionPlanMember[] = [];
  const cursors = new Set<string>();
  const memberKeys = new Set<string>();
  let cursor: string | undefined;
  for (let page = 1; ; page += 1) {
    if (page > maxPlanPages) {
      throw tooLarge(`the execution plan is over ${maxPlanPages} pages`);
    }
    const parsed = readPage(await fetchPage(cursor), suiteRevisionId);
    // Checked before the page is kept: the walk stops here rather than mapping the members of
    // a plan that is already over the cap. With the page and page-count caps this guard is a
    // backstop, but it must never let an over-cap plan reach the mapping.
    if (members.length + parsed.members.length > MAX_MEMBERS) {
      throw tooLarge(`the execution plan lists over ${MAX_MEMBERS} members`);
    }
    for (const member of parsed.members) {
      const key = `${member.case_revision_id}\u0000${member.variant_hash}`;
      if (memberKeys.has(key)) {
        throw planInvalid('the execution plan lists one case revision and variant twice');
      }
      memberKeys.add(key);
      members.push(member);
    }
    const next = parsed.next;
    if (next === null) {
      break;
    }
    if (next.length === 0) {
      throw planInvalid('the execution plan returned an empty cursor');
    }
    if (cursors.has(next)) {
      throw planInvalid('the execution plan repeated a cursor');
    }
    cursors.add(next);
    cursor = next;
  }
  if (members.length === 0) {
    throw planInvalid('the execution plan lists no members');
  }
  return members;
}

/**
 * One page, validated against `SuiteRevisionExecutionPlan`. A member's case id must be the
 * canonical lowercase form the report compares, so a case cannot be missed by case alone.
 */
function readPage(data: unknown, suiteRevisionId: string): PlanPage {
  const page = record(data);
  if (page === undefined || page['suite_revision_id'] !== suiteRevisionId) {
    throw planInvalid('the execution plan answered for another suite revision');
  }
  const rawMembers = page['members'];
  if (!Array.isArray(rawMembers)) {
    throw planInvalid('the execution plan page has no members list');
  }
  // The route's own limit is 500; refuse a larger page before any member is mapped, so a
  // crafted answer cannot spend the walk's budget on one oversized array.
  if (rawMembers.length > planPageLimit) {
    throw planInvalid(`the execution plan page lists over ${planPageLimit} members`);
  }
  const next = page['next_cursor'];
  if (next !== null && typeof next !== 'string') {
    throw planInvalid('the execution plan page has no next_cursor');
  }
  return { members: rawMembers.map(readMember), next };
}

/** One member: a lowercase canonical case id, a `sha256:` variant hash and a UUID revision. */
function readMember(raw: unknown): SuiteRevisionExecutionPlanMember {
  const member = record(raw);
  if (member === undefined) {
    throw planInvalid('the execution plan page has a malformed member');
  }
  const caseId = member['case_id'];
  const variantHash = member['variant_hash'];
  const caseRevisionId = member['case_revision_id'];
  if (typeof caseId !== 'string' || !isCanonicalCaseId(caseId) || caseId !== caseId.toLowerCase()) {
    throw planInvalid('the execution plan page has a malformed case id');
  }
  if (typeof variantHash !== 'string' || !VARIANT_HASH.test(variantHash)) {
    throw planInvalid('the execution plan page has a malformed variant hash');
  }
  if (typeof caseRevisionId !== 'string' || !UUID.test(caseRevisionId)) {
    throw planInvalid('the execution plan page has a malformed case revision id');
  }
  return { case_id: caseId, variant_hash: variantHash, case_revision_id: caseRevisionId };
}

function record(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}

function planInvalid(message: string): CliError {
  return new CliError(message, { code: 'manifest_plan_invalid' });
}

function tooLarge(message: string): CliError {
  return new CliError(message, { code: 'manifest_input_too_large' });
}
