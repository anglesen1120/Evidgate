import { createIntent } from '../../../core/http/intent';
import type { MembersStore } from './members.store';
import {
  mountMembersStore,
  FakeMembersDataService,
  PAGE,
  EMPTY_PAGE,
  CREATED,
  INVITE_TOKEN,
  ROTATED_TOKEN,
  PENDING,
  ISSUED,
  ROTATED,
  pageMember,
  roleId,
  settleBackground,
} from './members.store.test-support';

describe('MembersStore (09.7.1-T2)', () => {
  let data: FakeMembersDataService;
  let store: MembersStore;

  beforeEach(() => {
    ({ data, store } = mountMembersStore());
  });

  describe('a scope change while a mutation is in flight', () => {
    it('keeps the new org’s rows, still answers the token and points the next calls at it', async () => {
      await store.load('o-1');
      data.inviteManual = true;
      const inFlight = store.invite('newhire@acme.test', [], createIntent());
      expect(store.inviteState().phase).toBe('submitting');

      // The scope switches (the page closes its dialogs and reads the new org) while the invite
      // is still on its way to o-1.
      data.pages = [
        { items: EMPTY_PAGE.items, next_cursor: '1' },
        { items: PAGE.items, next_cursor: null },
      ];
      await store.load('o-2');
      data.pendingInvites[0]?.resolve(ISSUED);

      // The token still leaves with the answer (M2): the server will never serve it again, so
      // dropping it here would lose the invitation for good. Who may SHOW it is the dialog's own
      // stale-generation guard, not this store.
      await expect(inFlight).resolves.toEqual({
        membershipId: CREATED.id,
        inviteToken: INVITE_TOKEN,
      });

      // The invitation landed on o-1; its reload must not write into o-2's list, and there is no
      // background read either.
      expect(data.invites.map((entry) => entry.orgId)).toEqual(['o-1']);
      expect(data.membershipOrgs).toEqual(['o-1', 'o-2']);
      expect(store.members()).toEqual(EMPTY_PAGE.items);
      expect(store.hasMore()).toBe(true);
      expect(store.loadError()).toBeNull();

      // The store still addresses o-2 for the next mutation and the next page.
      data.inviteManual = false;
      await store.invite('second@acme.test', [], createIntent());
      await settleBackground();
      expect(data.invites.map((entry) => entry.orgId)).toEqual(['o-1', 'o-2']);

      await store.loadMore();
      expect(data.membershipOrgs).toEqual(['o-1', 'o-2', 'o-2', 'o-2']);
      expect(store.members().map((member) => member.id)).toEqual(
        PAGE.items.map((member) => member.id),
      );
    });

    it('skips the reload of a removal addressed to the org that is no longer shown', async () => {
      await store.load('o-1');
      data.removeManual = true;
      const inFlight = store.remove(PAGE.items[0]?.id ?? '', createIntent());

      data.pages = [EMPTY_PAGE];
      await store.load('o-2');
      data.pendingRemovals[0]?.resolve();

      await expect(inFlight).resolves.toBe(false);

      expect(data.removals.map((entry) => entry.orgId)).toEqual(['o-1']);
      expect(data.membershipOrgs).toEqual(['o-1', 'o-2']);
      expect(store.members()).toEqual(EMPTY_PAGE.items);
      // o-1's removal is not o-2's news: the switch invalidated that attempt, so it publishes
      // nothing and leaves no state for the dialogs o-2's page opens.
      expect(store.removeState()).toEqual({ phase: 'idle', error: null });
    });

    it('skips the rotate refresh but still answers the token when the scope switched', async () => {
      await store.load('o-1');
      data.resendManual = true;
      const inFlight = store.resend(PENDING.id, createIntent());
      expect(store.resendState().phase).toBe('submitting');

      data.pages = [EMPTY_PAGE];
      await store.load('o-2');
      data.pendingResends[0]?.resolve(ROTATED);

      // M2: the one-time token is the caller's, whatever the list now shows.
      await expect(inFlight).resolves.toEqual({
        membershipId: PENDING.id,
        inviteToken: ROTATED_TOKEN,
      });

      // No refresh read into another org's list, and the state still settles.
      expect(data.membershipGets).toEqual([]);
      expect(data.membershipOrgs).toEqual(['o-1', 'o-2']);
      expect(store.members()).toEqual(EMPTY_PAGE.items);
      expect(store.resendState()).toEqual({ phase: 'done', error: null });
    });

    it('skips the reload of a cancel addressed to the org that is no longer shown', async () => {
      await store.load('o-1');
      data.cancelManual = true;
      const inFlight = store.cancel(PENDING.id, createIntent());

      data.pages = [EMPTY_PAGE];
      await store.load('o-2');
      data.pendingCancels[0]?.resolve();

      await expect(inFlight).resolves.toBe(false);

      expect(data.cancels.map((entry) => entry.orgId)).toEqual(['o-1']);
      expect(data.membershipOrgs).toEqual(['o-1', 'o-2']);
      expect(store.members()).toEqual(EMPTY_PAGE.items);
      expect(store.cancelState()).toEqual({ phase: 'done', error: null });
    });

    it('skips the refresh of a role change addressed to the org that is no longer shown', async () => {
      await store.load('o-1');
      const target = pageMember(1);
      const viewer = roleId('viewer');
      data.grantManual = true;
      const inFlight = store.changeRoles(target.id, [
        { kind: 'grant', roleId: viewer, intent: createIntent() },
      ]);
      expect(store.editState().phase).toBe('submitting');

      data.pages = [EMPTY_PAGE];
      await store.load('o-2');
      data.settleGrant(0);

      await expect(inFlight).resolves.toBe(false);

      expect(data.grants.map((entry) => entry.orgId)).toEqual(['o-1']);
      // No refresh read and no reload: the list now belongs to another org.
      expect(data.membershipGets).toEqual([]);
      expect(data.membershipOrgs).toEqual(['o-1', 'o-2']);
      expect(store.members()).toEqual(EMPTY_PAGE.items);
      // The state still settles, with the confirmed change named: the dialog is not left busy.
      expect(store.editState()).toEqual({ phase: 'done', error: null });
      expect(store.editApplied()).toEqual([{ kind: 'grant', roleId: viewer }]);
    });
  });
  describe('a rejection that is not the server’s answer', () => {
    it('settles a refused-by-nobody invite as a generic unexpected failure', async () => {
      await store.load('o-1');
      const before = store.members();
      data.inviteFailure = new Error('adapter bug: boom');

      await expect(store.invite('newhire@acme.test', [], createIntent())).resolves.toBeNull();

      const state = store.inviteState();
      expect(state.phase).toBe('failed');
      expect(state.error?.kind).toBe('unexpected');
      expect(state.error?.status).toBe(0);
      expect(state.error?.code).toBe('request_unresolved');
      // The typed failure is the store's own, never the thrown value's text.
      expect(state.error?.message).not.toContain('boom');
      expect(JSON.stringify(state)).not.toContain('boom');
      // Nothing reloaded and nothing was removed from the screen.
      expect(data.membershipOrgs).toEqual(['o-1']);
      expect(store.members()).toBe(before);
      // The dialog can be reopened: it is no longer busy, and a reset starts a new attempt.
      store.resetMutations();
      expect(store.inviteState()).toEqual({ phase: 'idle', error: null });
    });

    it('settles a refused-by-nobody removal as a generic unexpected failure', async () => {
      await store.load('o-1');
      const before = store.members();
      data.removeFailure = new TypeError('adapter bug: boom');

      await expect(store.remove(PAGE.items[0]?.id ?? '', createIntent())).resolves.toBe(false);

      const state = store.removeState();
      expect(state.phase).toBe('failed');
      expect(state.error?.kind).toBe('unexpected');
      expect(state.error?.status).toBe(0);
      expect(state.error?.code).toBe('request_unresolved');
      expect(JSON.stringify(state)).not.toContain('boom');
      expect(data.membershipOrgs).toEqual(['o-1']);
      expect(store.members()).toBe(before);
      // Reopening the remove dialog goes through the same reset.
      store.resetMutations();
      expect(store.removeState()).toEqual({ phase: 'idle', error: null });
    });

    it('settles a refused-by-nobody role change as a generic unexpected failure', async () => {
      await store.load('o-1');
      const before = store.members();
      data.grantFailure = new TypeError('adapter bug: boom');

      await expect(
        store.changeRoles(pageMember(1).id, [
          { kind: 'grant', roleId: roleId('viewer'), intent: createIntent() },
        ]),
      ).resolves.toBe(false);

      const state = store.editState();
      expect(state.phase).toBe('failed');
      expect(state.error?.kind).toBe('unexpected');
      expect(state.error?.status).toBe(0);
      expect(state.error?.code).toBe('request_unresolved');
      // The typed failure is the store's own, never the thrown value's text.
      expect(JSON.stringify(state)).not.toContain('boom');
      // Nothing was confirmed, so nothing is reported as applied and nothing was refreshed.
      expect(store.editApplied()).toEqual([]);
      expect(data.membershipGets).toEqual([]);
      expect(data.membershipOrgs).toEqual(['o-1']);
      expect(store.members()).toBe(before);
      // Reopening the dialog starts clean instead of inheriting the failed attempt.
      store.resetMutations();
      expect(store.editState()).toEqual({ phase: 'idle', error: null });
    });
  });
});
