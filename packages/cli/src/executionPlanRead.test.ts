// The execution-plan walk's own caps, without a command around it: one page may not exceed the
// route's 500-member limit, and the walk accepts exactly `MAX_MEMBERS` across the allowed pages.
import type { SuiteRevisionExecutionPlanMember } from '@evidgate/sdk';
import { describe, expect, it } from 'vitest';

import { readExecutionPlan, type PlanPageFetch } from './executionPlanRead.js';
import { MAX_MEMBERS } from './manifestMapping.js';

const suiteRevision = '11111111-1111-4111-8111-111111111111';

/** The `n`th member: unique case and revision, one variant hash for every member. */
function member(n: number): SuiteRevisionExecutionPlanMember {
  const tail = String(n).padStart(12, '0');
  return {
    case_id: `22222222-2222-4222-8222-${tail}`,
    case_revision_id: `33333333-3333-4333-8333-${tail}`,
    variant_hash: `sha256:${'a'.repeat(64)}`,
  };
}

function page(
  members: readonly SuiteRevisionExecutionPlanMember[],
  next_cursor: string | null,
): unknown {
  return { suite_revision_id: suiteRevision, members: [...members], next_cursor };
}

describe('readExecutionPlan caps', () => {
  it('a_501_member_page_is_plan_invalid_before_a_further_request', async () => {
    const tooMany = Array.from({ length: 501 }, (_value, index) => member(index + 1));
    let calls = 0;
    const fetchPage: PlanPageFetch = () => {
      calls += 1;
      return Promise.resolve(page(tooMany, 'next'));
    };

    await expect(readExecutionPlan(suiteRevision, fetchPage)).rejects.toMatchObject({
      code: 'manifest_plan_invalid',
    });
    expect(calls).toBe(1);
  });

  it('twenty_pages_of_500_members_read_the_whole_10000_member_plan', async () => {
    const pages = MAX_MEMBERS / 500;
    const fetchPage: PlanPageFetch = (cursor) => {
      const index = cursor === undefined ? 0 : Number(cursor);
      expect(index).toBeLessThan(pages);
      const members = Array.from({ length: 500 }, (_value, offset) =>
        member(index * 500 + offset + 1),
      );
      return Promise.resolve(page(members, index + 1 === pages ? null : String(index + 1)));
    };

    const members = await readExecutionPlan(suiteRevision, fetchPage);

    expect(members).toHaveLength(MAX_MEMBERS);
    expect(members.at(-1)?.case_id).toBe(`22222222-2222-4222-8222-000000010000`);
  });
});
