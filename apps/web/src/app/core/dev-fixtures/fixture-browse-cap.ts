import type { CaseDto, ManualCaseListItemDto, NodeDto } from '@evidgate/sdk';
import type { FixtureEntry } from './fixture-catalog';
import { bulkCasePage } from './fixture-case-list-bulk';

export const BROWSE_CAP_VARIANT = 'browse-cap';
const TOTAL = 10_001;
const ROOT = '018f0000-0000-7000-8000-000000000101';
const ITEM = '018f0000-0000-7000-8000-000000000201';
const OPERATIONS = {
  cases: 'listCases',
  'authoring/manual-cases': 'listManualCases',
  'authoring/tree': 'listAuthoringTree',
} as const;
export type BrowseCapOperation = (typeof OPERATIONS)[keyof typeof OPERATIONS];

function id(index: number): string {
  return `018f0000-0000-7000-8000-${String(100_000 + index).padStart(12, '0')}`;
}

function manualCase(index: number): ManualCaseListItemDto {
  return {
    case_id: id(index),
    code: `CAP-${String(index + 1).padStart(5, '0')}`,
    checklist_item_id: ITEM,
    node_id: ROOT,
    title: `Retained manual case ${index + 1}`,
    priority: 'medium',
    state: 'draft',
    version: 1,
    linked_live_count: 0,
    linked_case_ids: [],
  };
}

function node(index: number): NodeDto {
  return {
    node_id: id(index),
    parent_id: null,
    kind: 'module',
    title: `Retained hierarchy node ${index + 1}`,
    position: index,
    version: 1,
    child_count: 0,
    item_count: 0,
  };
}

/** Computed pages keep the real retained-row bound and the request's server page size intact. */
export function browseCapFixturePage(
  operation: BrowseCapOperation,
  offset: number,
  limit: number,
): FixtureEntry {
  const seed = bulkCasePage(1).items[0];
  if (!seed) throw new Error('The computed catalog fixture must provide a case.');
  const length = Math.min(limit, Math.max(0, TOTAL - offset));
  const items: (CaseDto | ManualCaseListItemDto | NodeDto)[] = Array.from({ length }, (_, row) => {
    const index = offset + row;
    if (operation === 'listManualCases') return manualCase(index);
    if (operation === 'listAuthoringTree') return node(index);
    return {
      ...seed,
      id: id(index),
      annotation_id: `018f0000-0000-7000-8000-${String(200_000 + index).padStart(12, '0')}`,
      name: `Retained catalog case ${index + 1}`,
      file_path: `tests/retained-${index + 1}.spec.ts`,
    };
  });
  const next = offset + items.length;
  return {
    name: `${operation}-browse-cap-${offset}`,
    derived: true,
    document: {
      operation_id: operation,
      variant: BROWSE_CAP_VARIANT,
      status: 200,
      headers: { 'X-Request-Id': '018f0000-0000-7000-8000-00000000ca1f' },
      latency_ms: 0,
      body: { items, next_cursor: next < TOTAL ? `browse-cap-${operation}-${next}` : null },
    },
  };
}

export function findBrowseCapFixture(
  method: string,
  path: string,
  url: string,
  variantFor: (operation: string) => string,
): FixtureEntry | undefined {
  if (method !== 'GET') return undefined;
  const suffix = /^\/projects\/[^/]+\/(cases|authoring\/manual-cases|authoring\/tree)$/.exec(path)?.[1];
  const operation = Object.entries(OPERATIONS).find(([key]) => key === suffix)?.[1];
  if (!operation || variantFor(operation) !== BROWSE_CAP_VARIANT) return undefined;
  const query = new URL(url, 'http://fixture.invalid').searchParams;
  if (query.has('node_id') || query.has('parent_id') || query.has('item_id')) return undefined;
  const cursor = query.get('cursor');
  const prefix = `browse-cap-${operation}-`;
  if (cursor && !cursor.startsWith(prefix)) return undefined;
  const offset = cursor ? Number(cursor.slice(prefix.length)) : 0;
  const limit = Number(query.get('limit') ?? 50);
  if (!Number.isInteger(offset) || offset < 0 || offset >= TOTAL) return undefined;
  if (!Number.isInteger(limit) || limit < 1 || limit > 200) return undefined;
  return browseCapFixturePage(operation, offset, limit);
}
