import type { ManualCaseQuery } from '../../core/data/authoring-reads.service';

export type CaseQueryParams = Readonly<Record<string, string | null | undefined>>;
export interface AuthoringCaseFilters {
  readonly itemId: string;
  readonly state: NonNullable<ManualCaseQuery['state']> | '';
  readonly priority: NonNullable<ManualCaseQuery['priority']> | '';
  readonly covered: 'true' | 'false' | '';
  readonly q: string;
  readonly limit: number;
}
export const CASE_QUERY_KEYS = [
  'node_id',
  'item_id',
  'state',
  'priority',
  'covered',
  'q',
  'cursor',
  'limit',
] as const;
export const CASE_PAGE_SIZES = [25, 50, 100, 200] as const;
export const EMPTY_CASE_FILTERS: AuthoringCaseFilters = {
  itemId: '',
  state: '',
  priority: '',
  covered: '',
  q: '',
  limit: 50,
};

const CASE_UUID_SHAPE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The server counts the composed search, so a decomposed `e` + U+0301 is one point here too. */
const CASE_SEARCH_LIMIT = 200;

export function isCaseUuid(value: string): boolean {
  return CASE_UUID_SHAPE.test(value);
}

/** Counts composed search points in a loop: `Array.from` would materialise a huge input. */
export function queryTooLong(value: string): boolean {
  let points = 0;
  const iterator = value.normalize('NFC')[Symbol.iterator]();
  while (!iterator.next().done) {
    points += 1;
    if (points > CASE_SEARCH_LIMIT) return true;
  }
  return false;
}

export function caseFiltersFromQuery(raw: CaseQueryParams): AuthoringCaseFilters {
  const state = raw['state'];
  const priority = raw['priority'];
  const covered = raw['covered'];
  const limit = Number(raw['limit']);
  return {
    itemId: raw['item_id'] ?? '',
    state: state === 'draft' || state === 'approved' || state === 'retired' ? state : '',
    priority:
      priority === 'critical' || priority === 'high' || priority === 'medium' || priority === 'low'
        ? priority
        : '',
    covered: covered === 'true' || covered === 'false' ? covered : '',
    q: raw['q'] ?? '',
    limit: Number.isInteger(limit) && limit >= 1 && limit <= 200 ? limit : 50,
  };
}

export function caseFiltersToQuery(filters: AuthoringCaseFilters): Record<string, string> {
  const entries: [string, string][] = [
    ['item_id', filters.itemId],
    ['state', filters.state],
    ['priority', filters.priority],
    ['covered', filters.covered],
    ['q', filters.q],
    ['limit', String(filters.limit)],
  ];
  return Object.fromEntries(entries.filter(([, value]) => value !== ''));
}

export function manualCaseQuery(
  raw: CaseQueryParams,
  filters = caseFiltersFromQuery(raw),
): ManualCaseQuery {
  // Malformed URL values are announced by the form; they are never sent as filters, because a
  // silently ignored UUID would widen the result set to the whole scope.
  const nodeId = raw['node_id'];
  return {
    ...(nodeId !== undefined && nodeId !== null && isCaseUuid(nodeId) ? { node_id: nodeId } : {}),
    ...(filters.itemId && isCaseUuid(filters.itemId) ? { item_id: filters.itemId } : {}),
    ...(filters.state ? { state: filters.state } : {}),
    ...(filters.priority ? { priority: filters.priority } : {}),
    ...(filters.covered ? { covered: filters.covered === 'true' } : {}),
    ...(filters.q ? { q: filters.q } : {}),
    ...(raw['cursor'] ? { cursor: raw['cursor'] } : {}),
    limit: filters.limit,
  };
}
