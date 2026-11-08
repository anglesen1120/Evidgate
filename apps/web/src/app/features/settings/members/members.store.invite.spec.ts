import { createIntent } from '../../../core/http/intent';
import type { MembersStore } from './members.store';
import {
  mountMembersStore,
  FakeMembersDataService,
  PAGE,
  CREATED,
  INVITE_TOKEN,
  INVITE_EXISTS,
  roleId,
  typedFailure,
  settleBackground,
} from './members.store.test-support';

describe('MembersStore (09.7.1-T2)', () => {
  let data: FakeMembersDataService;
  let store: MembersStore;

  beforeEach(() => {
    ({ data, store } = mountMembersStore());
  });

  describe('invite', () => {
    it('sends { email, roles } with the caller’s intent and reloads the first page', async () => {
      data.pages = [{ items: [...PAGE.items, CREATED], next_cursor: null }];
      await store.load('o-1');
      const intent = createIntent();

      const invited = await store.invite('newhire@acme.test', [roleId('viewer')], intent);

      // The token comes back exactly once, on the call: it is a return value, not state.
      expect(invited).toEqual({ membershipId: CREATED.id, inviteToken: INVITE_TOKEN });
      expect(data.invites).toEqual([
        {
          orgId: 'o-1',
          body: { email: 'newhire@acme.test', roles: [roleId('viewer')] },
          intent,
        },
      ]);
      expect(store.inviteState()).toEqual({ phase: 'done', error: null });
      // The address travels in the body only: the store's own state never carries it.
      expect(JSON.stringify(store.inviteState())).not.toContain('newhire@acme.test');
      // Neither does any state carry the token the answer just handed back.
      expect(JSON.stringify(store.inviteState())).not.toContain(INVITE_TOKEN);
      // The reload started from the first page, so the new invite reaches the screen.
      await settleBackground();
      expect(data.membershipCursors).toEqual([undefined, undefined]);
      expect(store.members().map((member) => member.id)).toContain(CREATED.id);
    });

    it('answers the token before its list reload finishes, so a closing dialog keeps it', async () => {
      await store.load('o-1');
      // Every membership read is deferred by hand from here on: the reload stays on the wire.
      data.manual = true;

      const inFlight = store.invite('newhire@acme.test', [], createIntent());
      await settleBackground();

      // M2: the one plaintext the server serves once arrives without waiting for the list.
      await expect(inFlight).resolves.toEqual({
        membershipId: CREATED.id,
        inviteToken: INVITE_TOKEN,
      });
      expect(store.inviteState()).toEqual({ phase: 'done', error: null });
      // The reload this call started has ISSUED its read and is waiting on it (deferred by hand):
      // the token came back anyway.
      expect(data.membershipCursors).toEqual([undefined, undefined]);
      expect(data.pending).toHaveLength(1);

      // Let it land, so the deferred read does not outlive the spec.
      data.pending[0]?.resolve(PAGE);
      await settleBackground();
      expect(data.membershipCursors).toEqual([undefined, undefined]);
      expect(store.members().map((member) => member.id)).toEqual(
        PAGE.items.map((member) => member.id),
      );
    });

    it('answers inviteToken: null when the server replays a body with no token', async () => {
      await store.load('o-1');
      // An `Idempotency-Key` replay answers the stored body, which never held the plaintext.
      data.issued = { ...CREATED, invite_token: null };

      const invited = await store.invite('newhire@acme.test', [], createIntent());

      expect(invited).toEqual({ membershipId: CREATED.id, inviteToken: null });
      expect(store.inviteState()).toEqual({ phase: 'done', error: null });
      await settleBackground();
    });

    it('answers inviteToken: null for an EMPTY served token, so the dialog offers Resend', async () => {
      await store.load('o-1');
      // F2: `''` is absence, not a one-time value — the dialogs must not reveal nothing.
      data.issued = { ...CREATED, invite_token: '' };

      const invited = await store.invite('newhire@acme.test', [], createIntent());

      expect(invited).toEqual({ membershipId: CREATED.id, inviteToken: null });
      await settleBackground();
    });

    it('answers inviteToken: null for a non-string served token', async () => {
      await store.load('o-1');
      // F2: a broken adapter that echoes a number must not reach the reveal as `42`.
      data.issued = { ...CREATED, invite_token: 42 as unknown as string };

      const invited = await store.invite('newhire@acme.test', [], createIntent());

      expect(invited).toEqual({ membershipId: CREATED.id, inviteToken: null });
      await settleBackground();
    });

    it('omits roles when the dialog selected none', async () => {
      await store.load('o-1');

      await store.invite('newhire@acme.test', [], createIntent());

      expect(data.invites[0]?.body).toEqual({ email: 'newhire@acme.test' });
    });

    it('reuses the same Idempotency-Key when the same intent is retried', async () => {
      await store.load('o-1');
      const intent = createIntent();
      const refusal = typedFailure(INVITE_EXISTS, 'conflict', 409);
      data.inviteFailure = refusal;

      const refused = await store.invite('newhire@acme.test', [], intent);
      expect(refused).toBeNull();
      expect(store.inviteState()).toEqual({ phase: 'failed', error: refusal.error });

      data.inviteFailure = null;
      const retried = await store.invite('newhire@acme.test', [], intent);

      expect(retried).toEqual({ membershipId: CREATED.id, inviteToken: INVITE_TOKEN });
      expect(data.invites.map((entry) => entry.intent.idempotencyKey)).toEqual([
        intent.idempotencyKey,
        intent.idempotencyKey,
      ]);
    });

    it('surfaces 409 invite_exists verbatim and leaves the list alone', async () => {
      await store.load('o-1');
      data.inviteFailure = typedFailure(INVITE_EXISTS, 'conflict', 409);

      const invited = await store.invite('newhire@acme.test', [], createIntent());

      expect(invited).toBeNull();
      expect(store.inviteState().phase).toBe('failed');
      expect(store.inviteState().error?.code).toBe(INVITE_EXISTS.error.code);
      expect(store.inviteState().error?.message).toBe(INVITE_EXISTS.error.message);
      // No reload: the refused invite changed nothing on screen.
      expect(data.membershipCursors).toEqual([undefined]);
      expect(store.members()).toHaveLength(PAGE.items.length);
    });
  });
});
