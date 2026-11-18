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
  readonly status: 'loading' | 'ready' | 'error';
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
 * page keeps rendering while this reports its own state.
 */
export const CatalogMembersReader = signalStore(
  { providedIn: 'root' },
  withState<CatalogMembersState>({
    orgId: null,
    status: 'loading',
    failure: null,
    members: NO_MEMBERS,
  }),
  withProps((store, data = inject(MembersDataService)) => {
    /** Bumped by every read; a read of an older generation never writes state. */
    let generation = 0;
    const read = async (orgId: string): Promise<void> => {
      generation += 1;
      const sequence = generation;
      patchState(store, { orgId, status: 'loading', failure: null });
      const members = new Map<string, MembershipView>();
      const requested = new Set<string>();
      let cursor: string | undefined;
      for (let page = 0; page < MAX_CURSOR_PAGES; page += 1) {
        let response: MembershipListResponse;
        try {
          response = await data.listMemberships(orgId, cursor);
        } catch (error) {
          if (sequence !== generation) return;
          patchState(store, { status: 'error', failure: typedFailure(error) });
          return;
        }
        if (sequence !== generation) return;
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
      patchState(store, { status: 'error', failure: READ_FAILED });
    };
    return { _read: read };
  }),
  withComputed((store) => ({
    /** Memberships that can own a case: a pending invite has no user to own it yet. */
    assignable: computed(() =>
      [...store.members().values()].filter((member) => member.status === 'active'),
    ),
  })),
  withMethods((store) => ({
    /** Reads the org's memberships once; a second call for the same org keeps the read rows. */
    init(orgId: string): void {
      if (store.orgId() === orgId && store.status() === 'ready') return;
      void store._read(orgId);
    },
    /** Re-reads the current org, for the page's retry control. */
    reload(): void {
      const orgId = store.orgId();
      if (orgId === null) return;
      void store._read(orgId);
    },
  })),
);
