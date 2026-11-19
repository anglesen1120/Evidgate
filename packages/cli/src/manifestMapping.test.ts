import { describe, expect, it } from 'vitest';

import { CliError } from './errors.js';
import {
  MAX_MEMBERS,
  MAX_TESTS,
  mapTestsToMembers,
  type EnumeratedTest,
  type PlanMember,
} from './manifestMapping.js';

const caseId = '12345678-1234-1234-1234-123456789abc';
const variantHash = `sha256:${'a'.repeat(64)}`;
const member: PlanMember = {
  case_id: caseId,
  variant_hash: variantHash,
  case_revision_id: 'rev-1',
};
const plan = { suite_revision_id: 'suite-1', members: [member] };
const test: EnumeratedTest = {
  file: 'tests/checkout.spec.ts',
  titlePath: ['checkout', 'pays'],
  caseId,
  variantHash,
};

function refusal(
  tests: readonly EnumeratedTest[],
  members: readonly PlanMember[] = [member],
): CliError {
  try {
    mapTestsToMembers(tests, { ...plan, members });
  } catch (error) {
    expect(error).toBeInstanceOf(CliError);
    if (error instanceof CliError) {
      return error;
    }
    throw error;
  }
  throw new Error('the mapping returned entries where a refusal was expected');
}

function withoutCaseId(entry: EnumeratedTest): EnumeratedTest {
  const { file, titlePath, variantHash: hash } = entry;
  return { file, titlePath, variantHash: hash };
}

function conflictingIdentity(entry: EnumeratedTest): EnumeratedTest {
  const { file, titlePath, variantHash: hash } = entry;
  return { file, titlePath, variantHash: hash, identityConflict: true };
}

function uniqueId(index: number): string {
  return `12345678-1234-1234-1234-${index.toString(16).padStart(12, '0')}`;
}

describe('mapTestsToMembers', () => {
  it('all_tests_mapped_returns_suite_and_revisions_in_input_order', () => {
    const later = { ...test, file: 'tests/later.spec.ts' };
    expect(mapTestsToMembers([later, test], plan)).toEqual({
      suiteRevisionId: 'suite-1',
      entries: [
        { test: later, caseRevisionId: 'rev-1' },
        { test, caseRevisionId: 'rev-1' },
      ],
    });
  });

  it('test_without_annotation_refuses_whole_mapping_naming_file_and_title', () => {
    const error = refusal([test, withoutCaseId(test)]);
    expect(error.code).toBe('manifest_test_unmapped');
    expect(error.message).toContain(test.file);
    expect(error.message).toContain('checkout > pays');
  });

  it('case_absent_from_plan_refuses_unmapped_without_entries', () => {
    const error = refusal([test, { ...test, caseId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa' }]);
    expect(error.code).toBe('manifest_test_unmapped');
    expect(error.details).toHaveLength(1);
  });

  it('variant_mismatch_refuses_unmapped', () => {
    expect(refusal([{ ...test, variantHash: `sha256:${'b'.repeat(64)}` }]).code).toBe(
      'manifest_test_unmapped',
    );
  });

  it('two_members_for_one_key_refuse_ambiguous', () => {
    expect(refusal([test], [member, { ...member, case_revision_id: 'rev-2' }]).code).toBe(
      'manifest_test_ambiguous',
    );
  });

  it('conflicting_annotations_refuse_ambiguous', () => {
    expect(refusal([conflictingIdentity(test)]).code).toBe('manifest_test_ambiguous');
  });

  it('distinct_variants_for_one_case_are_not_ambiguous', () => {
    const members = [member, { ...member, variant_hash: `sha256:${'c'.repeat(64)}` }];
    expect(mapTestsToMembers([test], { ...plan, members }).entries).toHaveLength(1);
  });

  it('repeated_test_maps_independently_each_time', () => {
    expect(mapTestsToMembers([test, test], plan).entries).toHaveLength(2);
  });

  it('two_problem_classes_report_only_the_first_class', () => {
    const error = refusal([conflictingIdentity(test), withoutCaseId(test)]);
    expect(error.code).toBe('manifest_test_ambiguous');
    expect(error.details).toHaveLength(1);
  });

  it('offending_tests_are_capped_at_twenty_details', () => {
    const offenders = Array.from({ length: 25 }, () => withoutCaseId(test));
    const error = refusal(offenders);
    expect(error.details).toHaveLength(20);
  });

  it('tests_at_the_cap_map_and_one_over_refuses_too_large', () => {
    const tests = Array.from({ length: MAX_TESTS }, () => test);
    expect(mapTestsToMembers(tests, plan).entries).toHaveLength(MAX_TESTS);
    expect(refusal([...tests, test]).code).toBe('manifest_input_too_large');
  });

  it('members_at_the_cap_map_and_one_over_refuses_too_large', () => {
    const members = Array.from({ length: MAX_MEMBERS - 1 }, (_, index) => ({
      case_id: uniqueId(index + 1),
      variant_hash: variantHash,
      case_revision_id: `rev-${index}`,
    }));
    const atCap = mapTestsToMembers([test], { ...plan, members: [member, ...members] });
    expect(atCap.entries).toHaveLength(1);
    expect(refusal([test], [member, ...members, member]).code).toBe('manifest_input_too_large');
  });

  it('errors_name_no_variant_parameter_value', () => {
    const secret = 'private-parameter-value';
    const error = refusal([{ ...test, variantHash: secret }]);
    expect(`${error.message}\n${(error.details ?? []).join('\n')}`).not.toContain(secret);
  });
});
