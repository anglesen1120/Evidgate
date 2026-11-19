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

export function queryTooLong(value: string): boolean {
  return Array.from(value).length > 200;
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
  return {
    ...(raw['node_id'] ? { node_id: raw['node_id'] } : {}),
    ...(filters.itemId ? { item_id: filters.itemId } : {}),
    ...(filters.state ? { state: filters.state } : {}),
    ...(filters.priority ? { priority: filters.priority } : {}),
    ...(filters.covered ? { covered: filters.covered === 'true' } : {}),
    ...(filters.q ? { q: filters.q } : {}),
    ...(raw['cursor'] ? { cursor: raw['cursor'] } : {}),
    limit: filters.limit,
  };
}
