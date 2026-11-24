import { computed, signal } from '@angular/core';
import type { RunSummary } from '@evidgate/sdk';
import { vi } from 'vitest';
import type { ApiError } from '../../../../core/http/api-error';
import { SessionStore } from '../../../../core/session/session.store';
import { NarrowMutationPolicy } from '../../../../shared/ui/narrow';
import { RunDataService } from '../../data/run-data.service';
import { type RunListFilters } from '../../list/run-list.store';

export const emptyFilters: RunListFilters = { state: '', decision: '', scope: '', refName: '' };
export const run: RunSummary = {
  run_id: '018f0000-0000-7000-8000-000000000021',
  state: 'complete',
  created_at: '2026-10-27T00:00:00Z',
  ref: {
    kind: 'branch',
    name: 'main',
    pr_number: null,
    head_sha: null,
    base_sha: null,
    trigger_event: 'push',
    is_fork: false,
  },
  unverified_ref: false,
  decision: null,
  completeness: 'complete',
  parent_run_id: null,
  rerun_scope: null,
};

export function pageStore() {
  const filters = signal<RunListFilters>({ ...emptyFilters });
  const rows = signal<readonly RunSummary[]>([run]);
  const page = signal(1);
  const pageSize = signal(20);
  const cursor = signal('');
  return {
    filters,
    rows,
    page,
    pageSize,
    visibleRows: rows,
    status: signal<'idle' | 'loading' | 'ready' | 'error'>('ready'),
    loadError: signal<ApiError | null>(null),
    pagingError: signal<ApiError | null>(null),
    busy: signal(false),
    hasNext: signal(false),
    refRejected: signal(false),
    loadedCount: computed(() => rows().length),
    isEmpty: computed(() => rows().length === 0),
    isFiltered: computed(() => Object.values(filters()).some(Boolean)),
    init: vi.fn((_project: string, query: Record<string, string | undefined>) => {
      if (query['pageSize']) pageSize.set(Number(query['pageSize']));
      cursor.set(query['cursor'] ?? '');
      filters.set({
        state: (query['state'] ?? '') as RunListFilters['state'],
        decision: (query['decision'] ?? '') as RunListFilters['decision'],
        scope: (query['scope'] ?? '') as RunListFilters['scope'],
        refName: query['ref'] ?? '',
      });
    }),
    setFilters: vi.fn((patch: Partial<RunListFilters>) => {
      filters.update((current) => ({ ...current, ...patch }));
      page.set(1);
      cursor.set('');
    }),
    toQuery: () =>
      Object.fromEntries(
        Object.entries({
          state: filters().state,
          decision: filters().decision,
          scope: filters().scope,
          ref: filters().refName,
          cursor: cursor(),
          pageSize: pageSize() === 20 ? '' : String(pageSize()),
        }).filter(([, value]) => value !== ''),
      ),
    next: vi.fn<() => Promise<void>>(() => Promise.resolve()),
    previous: vi.fn<() => Promise<void>>(() => Promise.resolve()),
    retryPage: vi.fn<() => Promise<void>>(() => Promise.resolve()),
    setPageSize: vi.fn((size: number) => {
      pageSize.set(size);
      page.set(1);
      cursor.set('');
      return Promise.resolve();
    }),
    reload: vi.fn(),
  };
}

export function runCreatePageProviders(granted = false) {
  const allowed = signal(granted);
  const canMutate = signal(true);
  const createRun = vi.fn();
  return {
    allowed,
    canMutate,
    createRun,
    providers: [
      {
        provide: SessionStore,
        useValue: {
          has: () => allowed(),
          currentProject: () => ({ id: 'project-1' }),
          currentOrg: () => ({ id: 'org-1' }),
          onSessionEnd: () => () => undefined,
        },
      },
      { provide: NarrowMutationPolicy, useValue: { canMutate, allowsMutation: () => canMutate() } },
      {
        provide: RunDataService,
        useValue: { createRun, newIdempotencyKey: () => crypto.randomUUID() },
      },
    ],
  };
}
