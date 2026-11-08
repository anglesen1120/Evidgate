import type { MembershipView } from '@evidgate/sdk';
import { createIntent } from '../../../core/http/intent';
import type { MembersStore } from './members.store';
import {
  mountMembersStore,
  FakeMembersDataService,
  PAGE,
  LAST_ADMIN,
  SELF_ESCALATION,
  pageMember,
  roleId,
  typedFailure,
} from './members.store.test-support';

describe('MembersStore (09.7.1-T2)', () => {
  let data: FakeMembersDataService;
  let store: MembersStore;

  beforeEach(() => {
    ({ data, store } = mountMembersStore());
  });

  describe('changeRoles', () => {
    it('applies grants before revokes in the given order and shows the server’s roles', async () => {
      await store.load('o-1');
      const target = pageMember(1);
      const viewer = roleId('viewer');
      const qa = roleId('qa_engineer');
      const grantIntent = createIntent();
      const revokeIntent = createIntent();

      const changed = await store.changeRoles(target.id, [
        { kind: 'grant', roleId: viewer, intent: grantIntent },
        { kind: 'revoke', roleId: qa, intent: revokeIntent },
      ]);

      expect(changed).toBe(true);
      // One request per role, in the dialog's order, each with its own caller-owned key.
      expect(data.roleRequests).toEqual([`grant:${viewer}`, `revoke:${qa}`]);
      expect(data.grants[0]?.intent).toBe(grantIntent);
      expect(data.revokes[0]?.intent).toBe(revokeIntent);
      expect(store.editState()).toEqual({ phase: 'done', error: null });
      expect(store.editApplied()).toEqual([
        { kind: 'grant', roleId: viewer },
        { kind: 'revoke', roleId: qa },
      ]);
      // The row is the server's row, in its own position, replaced after one refresh read.
      expect(store.memberById(target.id)?.roles.map((role) => role.id)).toEqual([viewer]);
      expect(store.members().map((member) => member.id)).toEqual(PAGE.items.map((it) => it.id));
      expect(data.membershipGets).toEqual([{ orgId: 'o-1', membershipId: target.id }]);
      // Refreshed in place: no full reload, so the other rows and the cursor state stand.
      expect(data.membershipOrgs).toEqual(['o-1']);
    });

    it('forwards each change’s own intent, so two grants never share one key', async () => {
      await store.load('o-1');
      const first = createIntent();
      const second = createIntent();

      await store.changeRoles(pageMember(1).id, [
        { kind: 'grant', roleId: roleId('viewer'), intent: first },
        { kind: 'grant', roleId: roleId('ci_producer'), intent: second },
      ]);

      // The key belongs to the change, not to the attempt: one key on two different grants is the
      // server's 409 `idempotency_conflict` (00 §9.1, the operation is the command type), so the
      // store passes each change's own intent through and never mints or reuses one itself.
      expect(data.grants.map((entry) => entry.intent)).toEqual([first, second]);
      expect(new Set(data.grants.map((entry) => entry.intent.idempotencyKey)).size).toBe(2);
      expect(store.editApplied()).toHaveLength(2);
    });

    it('grant ok then revoke 409 last_admin: stops, keeps the grant and refreshes the row', async () => {
      await store.load('o-1');
      const admin = pageMember(0);
      const viewer = roleId('viewer');
      const tenantAdmin = roleId('tenant_admin');
      data.revokeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);

      const changed = await store.changeRoles(admin.id, [
        { kind: 'grant', roleId: viewer, intent: createIntent() },
        { kind: 'revoke', roleId: tenantAdmin, intent: createIntent() },
        // Never reached: the refusal above ends the attempt, so no third call is sent.
        { kind: 'grant', roleId: roleId('ci_producer'), intent: createIntent() },
      ]);

      expect(changed).toBe(false);
      expect(data.roleRequests).toEqual([`grant:${viewer}`, `revoke:${tenantAdmin}`]);
      expect(data.grants).toHaveLength(1);
      expect(data.revokes).toHaveLength(1);
      // The refusal is the server's own, read from `editState` exactly as it arrived.
      expect(store.editState().phase).toBe('failed');
      expect(store.editState().error?.status).toBe(409);
      expect(store.editState().error?.kind).toBe('conflict');
      expect(store.editState().error?.code).toBe('last_admin');
      expect(store.editState().error?.message).toBe(LAST_ADMIN.error.message);
      // The grant landed, so it is named as applied and the row shows it; the revoke did not.
      expect(store.editApplied()).toEqual([{ kind: 'grant', roleId: viewer }]);
      expect(store.memberById(admin.id)?.roles.map((role) => role.id)).toEqual([
        tenantAdmin,
        viewer,
      ]);
      expect(data.membershipGets).toEqual([{ orgId: 'o-1', membershipId: admin.id }]);
    });

    it('leaves the list exactly as it was when the first change is refused', async () => {
      await store.load('o-1');
      const before = store.members();
      data.revokeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);

      const changed = await store.changeRoles(pageMember(0).id, [
        { kind: 'revoke', roleId: roleId('tenant_admin'), intent: createIntent() },
      ]);

      expect(changed).toBe(false);
      expect(store.editState().error?.code).toBe(LAST_ADMIN.error.code);
      expect(store.editApplied()).toEqual([]);
      // Nothing was confirmed, so there is nothing to refresh and nothing to show as applied.
      expect(store.members()).toBe(before);
      expect(data.membershipGets).toEqual([]);
      expect(data.membershipOrgs).toEqual(['o-1']);
    });

    it('surfaces the 403 of the self-escalation rule verbatim', async () => {
      await store.load('o-1');
      const before = store.members();
      data.grantFailure = typedFailure(SELF_ESCALATION, 'forbidden', 403);

      const changed = await store.changeRoles(pageMember(0).id, [
        { kind: 'grant', roleId: roleId('release_approver'), intent: createIntent() },
      ]);

      expect(changed).toBe(false);
      const state = store.editState();
      expect(state.phase).toBe('failed');
      expect(state.error?.kind).toBe('forbidden');
      expect(state.error?.status).toBe(403);
      expect(state.error?.code).toBe(SELF_ESCALATION.error.code);
      expect(state.error?.message).toBe(SELF_ESCALATION.error.message);
      expect(store.editApplied()).toEqual([]);
      expect(store.members()).toBe(before);
    });

    it('reloads the list when the refresh read fails', async () => {
      await store.load('o-1');
      const target = pageMember(1);
      data.membershipGetFailure = typedFailure(SELF_ESCALATION, 'forbidden', 403);

      const changed = await store.changeRoles(target.id, [
        { kind: 'grant', roleId: roleId('viewer'), intent: createIntent() },
      ]);

      expect(changed).toBe(true);
      expect(data.membershipGets).toEqual([{ orgId: 'o-1', membershipId: target.id }]);
      // The row could not be re-read, so the store re-read the list instead of trusting it.
      expect(data.membershipOrgs).toEqual(['o-1', 'o-1']);
      expect(store.phase()).toBe('ready');
      expect(store.editState()).toEqual({ phase: 'done', error: null });
      expect(store.editApplied()).toEqual([{ kind: 'grant', roleId: roleId('viewer') }]);
    });

    it('reloads the list when the refreshed row is no longer on the pages it holds', async () => {
      await store.load('o-1');
      const stranger: MembershipView = { ...pageMember(1), id: 'm-not-listed' };
      data.memberViews.set(stranger.id, stranger);

      const changed = await store.changeRoles(stranger.id, [
        { kind: 'grant', roleId: roleId('viewer'), intent: createIntent() },
      ]);

      expect(changed).toBe(true);
      expect(data.membershipGets).toEqual([{ orgId: 'o-1', membershipId: stranger.id }]);
      expect(data.membershipOrgs).toEqual(['o-1', 'o-1']);
      expect(store.members().map((member) => member.id)).toEqual(PAGE.items.map((it) => it.id));
    });

    it('sends nothing and settles done when the draft equals the server state', async () => {
      await store.load('o-1');

      await expect(store.changeRoles(pageMember(1).id, [])).resolves.toBe(true);

      expect(data.roleRequests).toEqual([]);
      expect(data.membershipGets).toEqual([]);
      expect(store.editState()).toEqual({ phase: 'done', error: null });
      expect(store.editApplied()).toEqual([]);
    });

    it('answers false when no org list is loaded, because there is no scope to address', async () => {
      await expect(store.changeRoles(pageMember(1).id, [])).resolves.toBe(false);

      expect(data.roleRequests).toEqual([]);
      expect(store.editState()).toEqual({ phase: 'idle', error: null });
    });

    it('keeps the attempt submitting until the refresh read settles, then publishes it once', async () => {
      await store.load('o-1');
      const target = pageMember(1);
      const viewer = roleId('viewer');
      // The server confirmed the grant; its own refresh read is the part still on the wire.
      data.membershipGetManual = true;

      const inFlight = store.changeRoles(target.id, [
        { kind: 'grant', roleId: viewer, intent: createIntent() },
      ]);
      // Let the grant's promise chain advance to the refresh read.
      await new Promise((resolve) => setTimeout(resolve, 0));

      expect(data.roleRequests).toEqual([`grant:${viewer}`]);
      expect(data.pendingGets).toHaveLength(1);
      // Busy() is `phase === 'submitting'`: the dialog's Save and Cancel stay inert, so a second
      // save cannot re-send the confirmed change while `diff()` still reads the old row.
      expect(store.editState().phase).toBe('submitting');
      expect(store.editApplied()).toEqual([]);

      data.settleGet(0);
      await expect(inFlight).resolves.toBe(true);

      expect(store.editState()).toEqual({ phase: 'done', error: null });
      expect(store.editApplied()).toEqual([{ kind: 'grant', roleId: viewer }]);
      expect(store.memberById(target.id)?.roles.map((role) => role.id)).toContain(viewer);
    });

    it('keeps the row refresh but drops its own state when the dialog closed mid-flight', async () => {
      await store.load('o-1');
      const target = pageMember(1);
      const viewer = roleId('viewer');
      data.grantManual = true;
      const inFlight = store.changeRoles(target.id, [
        { kind: 'grant', roleId: viewer, intent: createIntent() },
      ]);
      expect(store.editState().phase).toBe('submitting');

      // The user closes the dialog (or another dialog reopens): the attempt is abandoned.
      store.resetMutations();
      data.settleGrant(0);

      await expect(inFlight).resolves.toBe(false);
      expect(store.editState()).toEqual({ phase: 'idle', error: null });
      expect(store.editApplied()).toEqual([]);
      // The change landed server-side, so the row still comes from the server.
      expect(store.memberById(target.id)?.roles.map((role) => role.id)).toEqual([
        roleId('qa_engineer'),
        viewer,
      ]);
    });
  });
});
