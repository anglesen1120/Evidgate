// The org's usage summary, shared by the Quota & retention tab and the app-shell badge.
//
// One `resource` owns the read, keyed on the active organization: an org switch refetches, and a
// sign-out or a revoked `usage:read` drops the value instead of keeping the previous tenant's
// counters on screen. Nothing is requested until a consumer calls `load()`, and `load()` is
// idempotent — the badge and the tab both call it.
//
// A refusal that arrives while a read is in flight asks for one trailing read when that read
// lands, and a reload keeps the counters already served on screen: the badge neither flaps nor
// re-announces on every refresh, while a first load, an org switch, an error or a null answer
// hides it.

import { computed, DestroyRef, inject, resource } from '@angular/core';
import type { UsageSummaryDto } from '@evidgate/sdk';
import { patchState, signalStore, withComputed, withProps, withState } from '@ngrx/signals';

import { QuotaDataService } from '../../../core/data/quota-data.service';
import { QuotaExceededRelay } from '../../../core/http/quota-exceeded-relay';
import { SessionStore } from '../../../core/session/session.store';
import { TenantStore } from '../../../core/session/tenant.store';

/** The wire summary the panel renders; nothing here reshapes it. */
export type UsageSummary = UsageSummaryDto;

/** `idle` before the first `load()` or while no org is active; `unavailable` on any failure. */
export type QuotaLoadState = 'idle' | 'loading' | 'ready' | 'unavailable';

/** What the badge shows: the counter that crossed the warning line, or the blocked one. */
export type QuotaBadge = {
  readonly severity: 'warn' | 'blocked';
  readonly metric: string;
  /** The counter that crossed the line, kept as the summary serves it: org-wide or one project. */
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

    // A refusal that lands while a read is in flight is answered by one more read when that one
    // settles, resolved or failed. The summary read never reports its own refusal
    // (`reportQuota: false`), so the trailing read cannot ask for another one by itself. The org
    // is kept with the request: a refresh asked for by one organization's read never runs against
    // the next organization's.
    let refreshPendingFor: string | null = null;

    type ReadOutcome = { readonly value: UsageSummaryDto | null } | { readonly error: unknown };

    /**
     * One read, reported instead of thrown: a failed read still has to answer the refusal that
     * landed while it was in flight, which a throw would skip.
     */
    async function readOnce(params: string, abortSignal: AbortSignal): Promise<ReadOutcome> {
      try {
        return { value: await data.summary(params, abortSignal) };
      } catch (error) {
        return { error };
      }
    }

    const summary = resource<UsageSummaryDto | null, string | undefined>({
      params: () => scope() ?? undefined,
      loader: async ({ params, abortSignal }) => {
        if (refreshPendingFor !== params) {
          refreshPendingFor = null;
        }
        let outcome = await readOnce(params, abortSignal);
        while (refreshPendingFor === params) {
          refreshPendingFor = null;
          outcome = await readOnce(params, abortSignal);
        }
        if ('error' in outcome) {
          throw outcome.error;
        }
        return outcome.value;
      },
    });

    /** Idempotent: the first call starts the read, later calls ask for the same request. */
    function load(): void {
      patchState(store, { started: true });
    }

    /**
     * Re-reads the summary. `resource.reload()` refuses while a read is in flight, so that
     * refusal is not dropped: it asks for exactly one trailing read, and a burst still costs one.
     */
    function reload(): void {
      if (!store.started()) {
        patchState(store, { started: true });
        return;
      }
      if (summary.isLoading()) {
        refreshPendingFor = orgId();
        return;
      }
      summary.reload();
    }

    // A refused write means a counter or limit moved since the badge last read it.
    const relay = inject(QuotaExceededRelay);
    const unregister = relay.register(reload);
    inject(DestroyRef).onDestroy(unregister);

    return { permitted, orgId, scope, summary, load, reload };
  }),
  withComputed((store) => {
    /**
     * The served summary, or `null` on a first load, an org switch, an error or a null answer, and
     * no counter is inferred from a missing measurement. A reload of the same request keeps the
     * counters already served, so a refusal does not make the badge flap or re-announce itself;
     * a previous organization's counters are never presented this way.
     */
    const usageSummary = computed<UsageSummary | null>(() => {
      if (store.scope() === null) {
        return null;
      }
      const status = store.summary.status();
      if (status !== 'reloading' && status !== 'resolved' && status !== 'local') {
        return null;
      }
      // `value()` throws for an errored stream, including a reload of a failed read.
      if (store.summary.error() !== undefined) {
        return null;
      }
      return store.summary.value() ?? null;
    });
    return {
      usageSummary,
      loadState: computed<QuotaLoadState>(() => {
        if (!store.started()) {
          return 'idle';
        }
        if (!store.permitted()) {
          return 'unavailable';
        }
        if (store.orgId() === null) {
          return 'idle';
        }
        if (store.summary.status() === 'loading') {
          return 'loading';
        }
        return usageSummary() === null ? 'unavailable' : 'ready';
      }),
    };
  }),
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
    for (const project of metric.projects) {
      collect(crossed, metric.metric, project.scope, project);
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
