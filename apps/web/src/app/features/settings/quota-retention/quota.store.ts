// The org's usage summary, shared by the Quota & retention tab and the app-shell badge.
//
// The resource clears rows on scope changes. Page navigation retains the served summary on
// failure; quota-refusal refreshes still coalesce into one trailing read while one is in flight.

import { computed, DestroyRef, inject } from '@angular/core';
import type { UsageSummaryDto } from '@evidgate/sdk';
import { patchState, signalStore, withComputed, withProps, withState } from '@ngrx/signals';

import { QuotaDataService } from '../../../core/data/quota-data.service';
import { QuotaExceededRelay } from '../../../core/http/quota-exceeded-relay';
import { SessionStore } from '../../../core/session/session.store';
import { TenantStore } from '../../../core/session/tenant.store';
import { quotaSummaryRead } from './quota-summary-resource';

/** The wire summary the panel renders; nothing here reshapes it. */
export type UsageSummary = UsageSummaryDto;

/** `unavailable` means no served summary; a failed page replacement retains the ready page. */
export type QuotaLoadState = 'idle' | 'loading' | 'ready' | 'unavailable';

/** What the badge shows: the counter that crossed the warning line, or the blocked one. */
export type QuotaBadge = {
  readonly severity: 'warn' | 'blocked';
  readonly metric: string;
  /** Which counter crossed: the tenant's own, or one of the org's projects (counted over all). */
  readonly scope: 'tenant' | 'project';
};

export const QuotaStore = signalStore(
  { providedIn: 'root' },
  withState({ started: false }),
  withProps((store) => {
    const tenant = inject(TenantStore);
    const session = inject(SessionStore);
    const data = inject(QuotaDataService);

    // No permission or no tenant means no request at all: a hidden badge is not a failed read.
    const permitted = computed(() => session.has('usage:read'));
    const orgId = computed(() => tenant.orgId());
    const scope = computed(() => (store.started() && permitted() ? orgId() : null));

    // The request itself (the resource, its loader and the trailing read a refusal asks for).
    const read = quotaSummaryRead({
      scope,
      orgId,
      started: () => store.started(),
      start: () => {
        patchState(store, { started: true });
      },
      data,
    });

    // A refused write means a counter or limit moved since the badge last read it.
    const relay = inject(QuotaExceededRelay);
    const unregister = relay.register(read.reload);
    inject(DestroyRef).onDestroy(unregister);

    return {
      permitted,
      orgId,
      scope,
      usageSummary: read.summary,
      load: read.load,
      reload: read.reload,
      projectPaging: read.paging,
      projectPagingError: read.pagingError,
      projectPage: read.page,
      projectPageSize: read.pageSize,
      hasNextProjectPage: read.hasNext,
      nextProjectPage: read.next,
      previousProjectPage: read.previous,
      retryProjectPage: read.retry,
      setProjectPageSize: read.setPageSize,
    };
  }),
  withComputed((store) => ({
    loadState: computed<QuotaLoadState>(() => {
      if (!store.started()) return 'idle';
      if (!store.permitted()) return 'unavailable';
      if (store.orgId() === null) return 'idle';
      if (store.usageSummary() !== null) return 'ready';
      return store.projectPaging() ? 'loading' : 'unavailable';
    }),
  })),
  withComputed((store) => ({ badge: computed(() => badgeFor(store.usageSummary())) })),
);

/**
 * The counter the badge names: a blocked counter wins over a warned one, the tenant counter comes
 * before a project's at equal severity, and ledger order breaks the remaining ties. A tenant
 * counter with no measurement (`used` null) cannot prove a threshold, so it is skipped —
 * "unavailable" is never read as a warning.
 */
function badgeFor(summary: UsageSummary | null): QuotaBadge | null {
  const crossed: QuotaBadge[] = [];
  for (const metric of summary?.metrics ?? []) {
    if (metric.used !== null) {
      collect(crossed, metric.metric, metric.scope, metric);
    }
    // The project rows are one page of the org's projects, so a project left on a page this read
    // never fetched has no row here. The server counts every crossing project of the org into the
    // metric's aggregate and repeats it on every page, so that count is what the badge reads.
    const status = metric.projects_status;
    if (status.blocked_count > 0) {
      crossed.push({ severity: 'blocked', metric: metric.metric, scope: 'project' });
    } else if (status.warned_count > 0) {
      crossed.push({ severity: 'warn', metric: metric.metric, scope: 'project' });
    }
  }
  return crossed.find((counter) => counter.severity === 'blocked') ?? crossed[0] ?? null;
}

/** Records the counter when it crossed a line; a measured counter below both lines adds nothing. */
function collect(
  crossed: QuotaBadge[],
  metric: string,
  scope: QuotaBadge['scope'],
  stamps: { readonly warned_at: string | null; readonly blocked_at: string | null },
): void {
  if (stamps.blocked_at !== null) {
    crossed.push({ severity: 'blocked', metric, scope });
  } else if (stamps.warned_at !== null) {
    crossed.push({ severity: 'warn', metric, scope });
  }
}
