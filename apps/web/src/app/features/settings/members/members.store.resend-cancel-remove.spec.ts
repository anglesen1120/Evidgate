import { createIntent } from '../../../core/http/intent';
import type { MembersStore } from './members.store';
import {
  mountMembersStore,
  FakeMembersDataService,
  PAGE,
  ROTATED_TOKEN,
  LAST_ADMIN,
  INVITE_NOT_PENDING,
  RATE_LIMITED,
  PENDING,
  ROTATED,
  typedFailure,
  settleBackground,
} from './members.store.test-support';

describe('MembersStore (09.7.1-T2)', () => {
  let data: FakeMembersDataService;
  let store: MembersStore;

  beforeEach(() => {
    ({ data, store } = mountMembersStore());
  });

  describe('resend', () => {
    it('rotates the invite, answers the new token and refreshes that one row', async () => {
      await store.load('o-1');
      const intent = createIntent();

      const rotated = await store.resend(PENDING.id, intent);

      // The new plaintext is the answer and nothing else.
      expect(rotated).toEqual({ membershipId: PENDING.id, inviteToken: ROTATED_TOKEN });
      expect(data.resends).toEqual([{ orgId: 'o-1', membershipId: PENDING.id, intent }]);
      expect(store.resendState()).toEqual({ phase: 'done', error: null });
      // The row comes from the server's own read, not from patchwork on the rotate response.
      await settleBackground();
      expect(data.membershipGets).toEqual([{ orgId: 'o-1', membershipId: PENDING.id }]);
      expect(store.members().map((member) => member.id)).toEqual(
        PAGE.items.map((member) => member.id),
      );
      expect(store.members()[3]?.status).toBe('pending');
      // No signal holds the token the call just returned.
      expect(JSON.stringify(store.resendState())).not.toContain(ROTATED_TOKEN);
    });

    it('answers the new token for an EMPTY served token as absence, never as an empty value', async () => {
      await store.load('o-1');
      // F2: the replay notice, not a one-time reveal of `''`.
      data.resendToken = '';

      const rotated = await store.resend(PENDING.id, createIntent());

      expect(rotated).toEqual({ membershipId: PENDING.id, inviteToken: null });
      await settleBackground();
    });

    it('answers absence for a non-string rotated token as well', async () => {
      await store.load('o-1');
      // The SDK type is the server's promise, so a non-string token is written past it on purpose.
      Reflect.set(data, 'resendToken', 7);

      const rotated = await store.resend(PENDING.id, createIntent());

      expect(rotated).toEqual({ membershipId: PENDING.id, inviteToken: null });
      await settleBackground();
    });

    it('surfaces 409 invite_not_pending verbatim and re-reads the list to drop the stale row', async () => {
      await store.load('o-1');
      const refusal = typedFailure(INVITE_NOT_PENDING, 'conflict', 409);
      data.resendFailure = refusal;
      // What the server's refusal means for the list: the row the user pressed is not pending.
      data.pages = [
        {
          items: PAGE.items.map((member) =>
            member.id === PENDING.id ? { ...member, status: 'active' as const } : member,
          ),
          next_cursor: null,
        },
      ];

      const rotated = await store.resend(PENDING.id, createIntent());

      expect(rotated).toBeNull();
      expect(store.resendState()).toEqual({ phase: 'failed', error: refusal.error });
      expect(store.resendState().error?.code).toBe('invite_not_pending');
      expect(store.resendState().error?.message).toBe(INVITE_NOT_PENDING.error.message);
      // L5: one re-read of the list, so the pending row (and its Resend action) disappears.
      expect(data.membershipCursors).toEqual([undefined, undefined]);
      expect(store.members()[3]?.status).toBe('active');
      // Only the refusal read: no row refresh of a rotate that never happened.
      expect(data.membershipGets).toEqual([]);
    });

    it('surfaces a 429 verbatim, so the rotate can offer the server’s countdown', async () => {
      await store.load('o-1');
      const refusal = typedFailure(RATE_LIMITED, 'rate_limited', 429);
      data.resendFailure = refusal;

      await expect(store.resend(PENDING.id, createIntent())).resolves.toBeNull();

      expect(store.resendState()).toEqual({ phase: 'failed', error: refusal.error });
      expect(store.resendState().error?.kind).toBe('rate_limited');
      expect(store.resendState().error?.retryable).toBe(true);
      // A throttle is not a stale row: the list on screen stands.
      expect(data.membershipGets).toEqual([]);
      expect(data.membershipCursors).toEqual([undefined]);
    });

    it('drops a detached row refresh that a newer load overtook, so no stale row lands', async () => {
      await store.load('o-1');
      // The rotate answers; its row refresh stays on the wire, deferred by hand.
      data.membershipGetManual = true;

      await expect(store.resend(PENDING.id, createIntent())).resolves.toEqual({
        membershipId: PENDING.id,
        inviteToken: ROTATED_TOKEN,
      });
      await settleBackground();
      expect(data.pendingGets).toHaveLength(1);

      // Meanwhile a newer load of the SAME org reads a list where that row has moved on.
      data.pages = [
        {
          items: PAGE.items.map((member) =>
            member.id === PENDING.id ? { ...member, status: 'active' as const } : member,
          ),
          next_cursor: null,
        },
      ];
      await store.load('o-1');
      expect(store.members()[3]?.status).toBe('active');

      // The overtaken read lands with the row as it was BEFORE that load: it writes nothing.
      data.settleGet(0);
      await settleBackground();
      expect(store.members()[3]?.status).toBe('active');
      // And it does not fall back to a reload either: the list was read exactly twice.
      expect(data.membershipCursors).toEqual([undefined, undefined]);
    });

    it('answers null without a second request while one rotate is submitting', async () => {
      await store.load('o-1');
      data.resendManual = true;
      const first = store.resend(PENDING.id, createIntent());
      expect(store.resendState().phase).toBe('submitting');

      // Two rotates would mint two tokens and kill the one the user is looking at.
      await expect(store.resend(PENDING.id, createIntent())).resolves.toBeNull();
      expect(data.resends).toHaveLength(1);

      data.pendingResends[0]?.resolve(ROTATED);
      await expect(first).resolves.toEqual({
        membershipId: PENDING.id,
        inviteToken: ROTATED_TOKEN,
      });
      expect(store.resendState()).toEqual({ phase: 'done', error: null });
      await settleBackground();
    });

    it('publishes done before the row refresh settles, and still refuses a second rotate', async () => {
      await store.load('o-1');
      // The server answered; its own refresh read is the part still on the wire.
      data.membershipGetManual = true;

      const rotated = store.resend(PENDING.id, createIntent());

      // M2: the token does not wait for the refresh, so a dialog that closes now still has it.
      await expect(rotated).resolves.toEqual({
        membershipId: PENDING.id,
        inviteToken: ROTATED_TOKEN,
      });
      expect(store.resendState()).toEqual({ phase: 'done', error: null });
      expect(data.membershipGets).toEqual([{ orgId: 'o-1', membershipId: PENDING.id }]);

      // The guard is the private lock, not the published phase: a second rotate within that window
      // would mint a second token and kill the one this call just handed back.
      await expect(store.resend(PENDING.id, createIntent())).resolves.toBeNull();
      expect(data.resends).toHaveLength(1);

      data.settleGet(0);
      await settleBackground();
      // The lock opens once the refresh lands, so a later, deliberate rotate is a new attempt.
      data.membershipGetManual = false;
      await expect(store.resend(PENDING.id, createIntent())).resolves.toEqual({
        membershipId: PENDING.id,
        inviteToken: ROTATED_TOKEN,
      });
      expect(data.resends).toHaveLength(2);
      await settleBackground();
    });

    it('rejects a non-API rejection as the generic unexpected failure', async () => {
      await store.load('o-1');
      data.resendFailure = new TypeError('adapter bug: boom');

      await expect(store.resend(PENDING.id, createIntent())).resolves.toBeNull();

      expect(store.resendState().phase).toBe('failed');
      expect(store.resendState().error?.kind).toBe('unexpected');
      expect(store.resendState().error?.status).toBe(0);
    });
  });
  describe('cancel', () => {
    it('cancels with the caller’s intent and reloads the list', async () => {
      await store.load('o-1');
      const intent = createIntent();
      // The server's answer to the cancel: the row is `revoked`, which the reload reads.
      data.pages = [
        {
          items: PAGE.items.map((member) =>
            member.id === PENDING.id ? { ...member, status: 'revoked' as const } : member,
          ),
          next_cursor: null,
        },
      ];

      const cancelled = await store.cancel(PENDING.id, intent);

      expect(cancelled).toBe(true);
      expect(data.cancels).toEqual([{ orgId: 'o-1', membershipId: PENDING.id, intent }]);
      expect(store.cancelState()).toEqual({ phase: 'done', error: null });
      expect(data.membershipCursors).toEqual([undefined, undefined]);
      expect(store.members()[3]?.status).toBe('revoked');
    });

    it('surfaces 409 invite_not_pending verbatim and re-reads the list to drop the stale row', async () => {
      await store.load('o-1');
      const refusal = typedFailure(INVITE_NOT_PENDING, 'conflict', 409);
      data.cancelFailure = refusal;
      // The server's answer means the row is no longer pending, which the re-read shows.
      data.pages = [
        {
          items: PAGE.items.map((member) =>
            member.id === PENDING.id ? { ...member, status: 'revoked' as const } : member,
          ),
          next_cursor: null,
        },
      ];

      const cancelled = await store.cancel(PENDING.id, createIntent());

      expect(cancelled).toBe(false);
      expect(store.cancelState()).toEqual({ phase: 'failed', error: refusal.error });
      expect(store.cancelState().error?.status).toBe(409);
      expect(store.cancelState().error?.code).toBe(INVITE_NOT_PENDING.error.code);
      expect(store.cancelState().error?.message).toBe(INVITE_NOT_PENDING.error.message);
      // L5: one re-read of the list, so the stale pending row and its Cancel action go.
      expect(data.membershipCursors).toEqual([undefined, undefined]);
      expect(store.members()[3]?.status).toBe('revoked');
    });

    it('rejects a non-API rejection as the generic unexpected failure', async () => {
      await store.load('o-1');
      data.cancelFailure = new Error('adapter bug: boom');

      await expect(store.cancel(PENDING.id, createIntent())).resolves.toBe(false);

      expect(store.cancelState().phase).toBe('failed');
      expect(store.cancelState().error?.kind).toBe('unexpected');
    });
  });
  describe('remove', () => {
    it('removes with the caller’s intent and reloads on success', async () => {
      data.pages = [{ items: PAGE.items.slice(0, 1), next_cursor: null }];
      await store.load('o-1');
      const target = PAGE.items[1];
      const intent = createIntent();

      const removed = await store.remove(target?.id ?? '', intent);

      expect(removed).toBe(true);
      expect(data.removals).toEqual([{ orgId: 'o-1', membershipId: target?.id, intent }]);
      expect(store.removeState()).toEqual({ phase: 'done', error: null });
      expect(data.membershipCursors).toEqual([undefined, undefined]);
    });

    it('surfaces 409 last_admin verbatim and leaves the list unchanged (AC2)', async () => {
      await store.load('o-1');
      const before = store.members();
      data.removeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);

      const removed = await store.remove(PAGE.items[0]?.id ?? '', createIntent());

      expect(removed).toBe(false);
      expect(store.removeState().phase).toBe('failed');
      expect(store.removeState().error?.status).toBe(409);
      expect(store.removeState().error?.code).toBe('last_admin');
      expect(store.removeState().error?.message).toBe(LAST_ADMIN.error.message);
      // The rows on screen are exactly the ones read before the attempt.
      expect(store.members()).toBe(before);
      expect(data.membershipCursors).toEqual([undefined]);
    });
  });
});
