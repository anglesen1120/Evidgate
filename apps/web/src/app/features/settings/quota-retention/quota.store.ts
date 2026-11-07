// The org's usage summary, shared by the Quota & retention tab and the app-shell badge.
//
// One `resource` owns the read, keyed on the active organization: an org switch refetches, and a
// sign-out or a revoked `usage:read` drops the value instead of keeping the previous tenant's
// counters on screen. Nothing is requested until a consumer calls `load()`, and `load()` is
// idempotent — the badge and the tab both call it.

import { computed, DestroyRef, inject, resource } from '@angular/core';
import type { UsageSummaryDto } from '@evidgate/sdk';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';

import { QuotaDataService } from '../../../core/data/quota-data.service';
import { QuotaExceededRelay } from '../../../core/http/quota-exceeded-relay';
import { SessionStore } from '../../../core/session/session.store';
import { TenantStore } from '../../../core/session/tenant.store';

/** The wire summary the panel renders; nothing here reshapes it. */
export type UsageSummary = UsageSummaryDto;

/** `idle` before the first `load()` or while no org is active; `unavailable` on any failure. */
export type QuotaLoadState = 'idle' | 'loading' | 'ready' | 'unavailable';

/** What the badge shows: the metric that crossed the warning line, or the blocked one. */
export type QuotaBadge = { readonly severity: 'warn' | 'blocked'; readonly metric: string };

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
    const summary = resource<UsageSummaryDto | null, string | undefined>({
      params: () => scope() ?? undefined,
      loader: ({ params, abortSignal }) => data.summary(params, abortSignal),
    });
    return { permitted, orgId, scope, summary };
  }),
  withComputed((store) => ({
    /**
     * The served summary, or `null` while loading and on any failure. A stale body is never
     * presented as the current one, and no counter is inferred from a missing measurement.
     */
    usageSummary: computed<UsageSummary | null>(() => {
      if (store.scope() === null) {
        return null;
      }
      if (store.summary.isLoading() || store.summary.error() !== undefined) {
        return null;
      }
      return store.summary.hasValue() ? store.summary.value() : null;
    }),
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
      if (store.summary.isLoading()) {
        return 'loading';
      }
      if (store.summary.error() !== undefined || !store.summary.hasValue()) {
        return 'unavailable';
      }
      return store.summary.value() === null ? 'unavailable' : 'ready';
    }),
  })),
  withComputed((store) => ({ badge: computed(() => badgeFor(store.usageSummary())) })),
  withMethods((store) => {
    const relay = inject(QuotaExceededRelay);

    /** Idempotent: the first call starts the read, later calls ask for the same request. */
    function load(): void {
      patchState(store, { started: true });
    }

    /** Re-reads the summary; `resource.reload` is a no-op while a read is already in flight. */
    function reload(): void {
      if (!store.started()) {
        patchState(store, { started: true });
        return;
      }
      store.summary.reload();
    }

    // A refused write means a counter or limit moved since the badge last read it. The summary
    // read never reports its own refusal, so this signal cannot feed itself.
    const unregister = relay.register(reload);
    inject(DestroyRef).onDestroy(unregister);

    return { load, reload };
  }),
);

/**
 * The metric the badge names: a blocked counter wins over a warned one, and ledger order breaks
 * ties. A counter with no measurement (`used` null) cannot prove a threshold, so it is skipped —
 * "unavailable" is never read as a warning.
 */
function badgeFor(summary: UsageSummary | null): QuotaBadge | null {
  const measured = summary?.metrics.filter((metric) => metric.used !== null) ?? [];
  const blocked = measured.find((metric) => metric.blocked_at !== null);
  if (blocked !== undefined) {
    return { severity: 'blocked', metric: blocked.metric };
  }
  const warned = measured.find((metric) => metric.warned_at !== null);
  return warned === undefined ? null : { severity: 'warn', metric: warned.metric };
}
