import { computed, inject, linkedSignal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, type ParamMap, type Params } from '@angular/router';
import { signalStore, withComputed, withMethods, withProps } from '@ngrx/signals';
import { combineLatest, distinctUntilChanged, map, of, switchMap, timer } from 'rxjs';

import { INBOX_COPY as c } from './failure-inbox.copy';
import {
  FILTERS,
  INCIDENTS,
  type FilterKey,
  type IncidentId,
  type IncidentViewModel,
} from './failure-inbox.fixture';

const PAGE_SIZE = 3;
const STATES = [
  'default',
  'empty',
  'loading',
  'error',
  '401',
  '403',
  '404',
  '412',
  '429',
  'nomatch',
  'collect',
  'closed',
  'model',
  'submitting',
  'saved',
  'snapshot',
  'latest',
] as const;
export type InboxState = (typeof STATES)[number];
function readState(q: ParamMap): InboxState {
  const state = q.get('state');
  if (state === 'permission-denied') return '403';
  if (
    q.has('id') &&
    !INCIDENTS.some((r) => r.id === q.get('id')) &&
    state !== '403' &&
    state !== '401'
  )
    return '404';
  return STATES.find((value) => value === state) ?? 'default';
}
function rowsFor(state: InboxState): readonly IncidentViewModel[] {
  const owner = state === 'saved' ? 'minh' : state === 'latest' ? 'linh' : null;
  return owner
    ? INCIDENTS.map((row) => (row.id === 'inc-91' ? { ...row, owner } : row))
    : INCIDENTS;
}
function matches(row: IncidentViewModel, q: ParamMap): boolean {
  const status = q.get('status');
  if (status && status !== row.status) return false;
  for (const filter of FILTERS) {
    const value = filter.key === 'label' && q.get('drift') ? 'test_drift' : q.get(filter.key);
    if (value && row[filter.key] !== value) return false;
  }
  const search = q.get('q')?.trim().toLocaleLowerCase();
  return (
    !search ||
    [
      row.id,
      row.title,
      row.revision,
      row.label,
      row.caseRef,
      c[row.severity],
      c[row.owner],
      c[row.status],
      c[row.context],
    ].some((value) => value?.toLocaleLowerCase().includes(search))
  );
}

export const FailureInboxStore = signalStore(
  withProps(() => {
    const route = inject(ActivatedRoute);
    const router = inject(Router);
    const query = toSignal(route.queryParamMap, { requireSync: true });
    const scope = toSignal(
      combineLatest(route.pathFromRoot.map((r) => r.paramMap)).pipe(
        map((maps) => {
          const org = maps.find((m) => m.has('orgId'))?.get('orgId');
          const project = maps.find((m) => m.has('projectId'))?.get('projectId');
          return org && project ? ['orgs', org, 'projects', project] : null;
        }),
      ),
      { requireSync: true },
    );
    const retryEligible = toSignal(
      route.queryParamMap.pipe(
        map(readState),
        distinctUntilChanged(),
        switchMap((state) => (state === '429' ? timer(60_000).pipe(map(() => true)) : of(false))),
      ),
      { initialValue: false },
    );
    return { query, scope, retryEligible, _route: route, _router: router };
  }),
  withComputed((s) => ({ state: computed(() => readState(s.query())) })),
  withComputed((s) => ({
    records: computed(() => rowsFor(s.state())),
    showQueue: computed(() => ['default', 'saved', 'latest', 'submitting'].includes(s.state())),
  })),
  withComputed((s) => ({
    matches: computed(() => s.records().filter((row) => matches(row, s.query()))),
  })),
  withComputed((s) => ({
    page: computed(() => {
      const index = s.matches().findIndex((row) => row.id === s.query().get('id'));
      if (index >= 0) return Math.floor(index / PAGE_SIZE);
      return s.query().get('cursor') === '2' && s.matches().length > PAGE_SIZE ? 1 : 0;
    }),
  })),
  withComputed((s) => ({
    visible: computed(() => s.matches().slice(s.page() * PAGE_SIZE, (s.page() + 1) * PAGE_SIZE)),
  })),
  withComputed((s) => ({
    active: computed(
      () => s.visible().find((row) => row.id === s.query().get('id')) ?? s.visible()[0] ?? null,
    ),
  })),
  withProps((s) => ({
    checked: linkedSignal<
      { scope: string[] | null; rows: readonly IncidentViewModel[]; state: InboxState },
      readonly IncidentId[]
    >({
      source: () => ({ scope: s.scope(), rows: s.visible(), state: s.state() }),
      computation: (current, previous): readonly IncidentId[] => {
        if (
          current.scope !== previous?.source.scope ||
          ['401', '403', '404', 'empty'].includes(current.state)
        )
          return [];
        return previous?.value.filter((id) => current.rows.some((row) => row.id === id)) ?? [];
      },
    }),
    confirmation: linkedSignal(() => {
      s.query();
      s.scope();
      return '';
    }),
  })),
  withMethods((s) => {
    function update(params: Params): void {
      void s._router.navigate([], {
        relativeTo: s._route,
        queryParams: params,
        queryParamsHandling: 'merge',
        replaceUrl: true,
      });
    }
    return {
      filter(key: FilterKey, value: string): void {
        update({
          [key]: !value || (key !== 'q' && value === 'all') ? null : value,
          cursor: null,
          id: null,
        });
      },
      clear(): void {
        update({
          ...Object.fromEntries(FILTERS.map((f) => [f.key, null])),
          status: null,
          q: null,
          cursor: null,
          id: null,
          state: null,
        });
      },
      paginate(next: boolean): void {
        update({ cursor: next ? '2' : null, id: null });
      },
      pick(id: IncidentId): void {
        update({ id });
      },
      recover(state: InboxState = 'default'): void {
        update({ state: state === 'default' ? null : state });
      },
      toggle(id: IncidentId, checked: boolean): void {
        s.checked.update((ids) =>
          checked ? [...ids.filter((item) => item !== id), id] : ids.filter((item) => item !== id),
        );
        s.confirmation.set('');
      },
      path(screen: string): string[] | null {
        const scope = s.scope();
        return scope ? ['/dev', screen, ...scope] : null;
      },
      returnUrl(): string {
        return s._router.url;
      },
    };
  }),
);
