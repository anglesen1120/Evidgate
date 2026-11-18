// A computed case page larger than the 200-row threshold: the shared table virtualises above it
// (ADR-0011), so a review needs rows without a 200-entry corpus file. Dev-only, like the rest of
// `core/dev-fixtures`; ids are fixed so they stay valid uuids and a spec can name a row.

import type { CaseDto, CaseListResponse } from '@evidgate/sdk';
import type { FixtureEntry } from './fixture-catalog-entries';

/** One past the threshold the shared virtual table switches on. */
export const BULK_CASE_ROW_COUNT = 201;

/** `index` (1-based) as the last uuid segment, zero-padded so every row keeps a uuid shape. */
function bulkId(prefix: string, index: number): string {
  return `018f0000-0000-7000-8000-${prefix}${String(index).padStart(4, '0')}`;
}

/** A page of `count` active cases, in server order, with no continuation. */
export function bulkCasePage(count: number = BULK_CASE_ROW_COUNT): CaseListResponse {
  const items: CaseDto[] = [];
  for (let index = 1; index <= count; index += 1) {
    items.push({
      id: bulkId('0000000e', index),
      org_id: '018f0000-0000-7000-8000-000000000001',
      project_id: '018f0000-0000-7000-8000-000000000011',
      annotation_id: bulkId('0000000f', index),
      name: `Bulk case ${index}`,
      framework: 'playwright',
      language: 'typescript',
      file_path: `tests/bulk-${index}.spec.ts`,
      status: 'active',
      owner_membership_id: null,
      labels: [],
      first_seen_commit: '9af317c',
      last_seen_commit: '9af317c',
      created_at: '2026-10-20T08:00:00Z',
      updated_at: '2026-10-20T08:00:00Z',
      version: 1,
      requirement_ref: null,
    });
  }
  return { items, next_cursor: null };
}

/** The variant a `?fixture.variant.listCases=bulk` toggle names. */
export const BULK_CASE_VARIANT = 'bulk';

/**
 * The computed entry that variant answers: one page past the virtualisation threshold, so the
 * shared table's windowed rendering can be reviewed without a 201-entry corpus file. It stays out
 * of `FIXTURE_CATALOG` because no directory holds it, exactly as the usage-summary specimens do.
 */
export function findBulkCaseListFixture(variant: string): FixtureEntry | undefined {
  if (variant !== BULK_CASE_VARIANT) return undefined;
  return {
    name: 'catalog-cases-bulk',
    document: {
      operation_id: 'listCases',
      variant,
      status: 200,
      headers: { 'X-Request-Id': '018f0000-0000-7000-8000-00000000ca1f' },
      latency_ms: 0,
      body: bulkCasePage(),
    },
  };
}
