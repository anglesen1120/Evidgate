import { computed, inject } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import type { MembershipListResponse, MembershipView } from '@evidgate/sdk';
import { isApiRequestError, type ApiError } from '../../core/http/api-error';
import { MembersDataService } from '../../core/data/members-data.service';
import { MAX_CURSOR_PAGES } from '../../core/data/identity-data.service';

/** Enough labels for a list, no second members screen: the whole org's memberships. */
interface CatalogMembersState {
  readonly orgId: string | null;
  readonly status: 'idle' | 'loading' | 'ready' | 'error';
  readonly failure: ApiError | null;
  readonly members: ReadonlyMap<string, MembershipView>;
}
const NO_MEMBERS: ReadonlyMap<string, MembershipView> = new Map();

const READ_FAILED: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'members_unresolved',
  message: $localize`:@@catalog.members-unresolved:The member names did not load. The owner is shown by id.`,
  requestId: null,
  retryable: true,
  details: [],
  retryAfterSeconds: null,
};

function typedFailure(error: unknown): ApiError {
  return isApiRequestError(error) ? error.error : READ_FAILED;
}

/**
 * The membership labels a case row needs: id to `MembershipView`, read once per org through the
 * members data service. A failed read is not fatal — the row still shows the owner id — so the
 * page keeps rendering while this reports its own state. A read is cancellable at its page
 * boundary: `clear()` on leaving the page stops the next page from being requested, and a late
 * answer never writes the members of an org the page has left.
 */
export const CatalogMembersReader = signalStore(
  { providedIn: 'root' },
  withState<CatalogMembersState>({
    orgId: null,
    status: 'idle',
    failure: null,
    members: NO_MEMBERS,
  }),
  withProps((store, data = inject(MembersDataService)) => {
    /** Bumped by every read and by `clear()`; an older read never writes state. */
    let generation = 0;
    let inFlight: AbortController | null = null;
    const read = async (orgId: string): Promise<void> => {
      inFlight?.abort();
      const controller = new AbortController();
      inFlight = controller;
      generation += 1;
      const sequence = generation;
      // An org change drops the previous org's map at once: a label must never cross orgs.
      patchState(store, { orgId, status: 'loading', failure: null, members: NO_MEMBERS });
      const members = new Map<string, MembershipView>();
      const requested = new Set<string>();
      let cursor: string | undefined;
      const cancelled = (): boolean => controller.signal.aborted || sequence !== generation;
      for (let page = 0; page < MAX_CURSOR_PAGES; page += 1) {
        if (cancelled()) return;
        let response: MembershipListResponse;
        try {
          response = await data.listMemberships(orgId, cursor);
        } catch (error) {
          if (cancelled()) return;
          patchState(store, { status: 'error', failure: typedFailure(error) });
          return;
        }
        if (cancelled()) return;
        for (const member of response.items) members.set(member.id, member);
        const next = response.next_cursor;
        if (next === null) {
          patchState(store, { status: 'ready', failure: null, members });
          return;
        }
        // A cursor the server hands back a second time is a contract violation, not a page.
        if (requested.has(next)) {
          patchState(store, { status: 'error', failure: READ_FAILED });
          return;
        }
        requested.add(next);
        cursor = next;
      }
      if (cancelled()) return;
      patchState(store, { status: 'error', failure: READ_FAILED });
    };
    return {
      _read: read,
      _cancel: (): void => {
        generation += 1;
      },
      _abort: (): void => {
        inFlight?.abort();
      },
    };
  }),
  withComputed((store) => ({
    /** Memberships that can own a case: a pending invite has no user to own it yet. */
    assignable: computed(() =>
      [...store.members().values()].filter((member) => member.status === 'active'),
    ),
  })),
  withMethods((store) => ({
    /**
     * Reads the org's memberships once. A call for the org already loaded, or already loading,
     * keeps the rows in hand; an org change starts a fresh read.
     */
    init(orgId: string): void {
      if (store.orgId() === orgId && store.status() !== 'error') return;
      void store._read(orgId);
    },
    /** Re-reads the current org, for the page's retry control. */
    reload(): void {
      const orgId = store.orgId();
      if (orgId === null) return;
      void store._read(orgId);
    },
    /** Stops the loop and forgets the org: the page calls this when it leaves or loses permission. */
    clear(): void {
      store._cancel();
      store._abort();
      patchState(store, { orgId: null, status: 'idle', failure: null, members: NO_MEMBERS });
    },
  })),
);
