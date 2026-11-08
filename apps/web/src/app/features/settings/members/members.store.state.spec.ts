import { createIntent } from '../../../core/http/intent';
import type { MembersStore } from './members.store';
import {
  mountMembersStore,
  FakeMembersDataService,
  PAGE,
  CREATED,
  INVITE_TOKEN,
  ROTATED_TOKEN,
  LAST_ADMIN,
  INVITE_NOT_PENDING,
  PENDING,
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

  it('resetMutations() returns every dialog to idle', async () => {
    await store.load('o-1');
    data.removeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);
    await store.remove(PAGE.items[0]?.id ?? '', createIntent());
    data.revokeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);
    await store.changeRoles(pageMember(0).id, [
      { kind: 'revoke', roleId: roleId('tenant_admin'), intent: createIntent() },
    ]);
    data.resendFailure = typedFailure(INVITE_NOT_PENDING, 'conflict', 409);
    await store.resend(PENDING.id, createIntent());
    data.cancelFailure = typedFailure(INVITE_NOT_PENDING, 'conflict', 409);
    await store.cancel(PENDING.id, createIntent());
    expect(store.editState().phase).toBe('failed');
    expect(store.resendState().phase).toBe('failed');
    expect(store.cancelState().phase).toBe('failed');

    store.resetMutations();

    expect(store.inviteState()).toEqual({ phase: 'idle', error: null });
    expect(store.resendState()).toEqual({ phase: 'idle', error: null });
    expect(store.cancelState()).toEqual({ phase: 'idle', error: null });
    expect(store.removeState()).toEqual({ phase: 'idle', error: null });
    expect(store.editState()).toEqual({ phase: 'idle', error: null });
    expect(store.editApplied()).toEqual([]);
  });
  it('holds no secret-shaped field in any of its signals (D5, S3)', async () => {
    await store.load('o-1');
    const issued = await store.invite('newhire@acme.test', [roleId('viewer')], createIntent());
    const rotated = await store.resend(PENDING.id, createIntent());
    await store.cancel(PENDING.id, createIntent());
    data.removeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);
    await store.remove(PAGE.items[0]?.id ?? '', createIntent());
    data.revokeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);
    await store.changeRoles(pageMember(0).id, [
      { kind: 'grant', roleId: roleId('viewer'), intent: createIntent() },
      { kind: 'revoke', roleId: roleId('tenant_admin'), intent: createIntent() },
    ]);

    // The two plaintexts the server served were handed to the caller, exactly once each.
    expect(issued).toEqual({ membershipId: CREATED.id, inviteToken: INVITE_TOKEN });
    expect(rotated).toEqual({ membershipId: PENDING.id, inviteToken: ROTATED_TOKEN });

    const keys = new Set<string>();
    const collect = (value: unknown): void => {
      if (Array.isArray(value)) {
        for (const item of value) collect(item);
        return;
      }
      if (value instanceof Set) {
        for (const item of value) collect(item);
        return;
      }
      if (value instanceof Map) {
        for (const item of value.values()) collect(item);
        return;
      }
      if (value !== null && typeof value === 'object') {
        for (const [key, child] of Object.entries(value)) {
          keys.add(key);
          collect(child);
        }
      }
    };
    const signals = [
      store.members(),
      store.roles(),
      store.catalogue(),
      store.phase(),
      store.loadError(),
      store.hasMore(),
      store.matrix(),
      store.inviteState(),
      store.resendState(),
      store.cancelState(),
      store.removeState(),
      store.editState(),
      store.editApplied(),
    ];
    collect(signals);

    // The state was serialised for real: a member, a role and a refusal are in it.
    expect(keys.has('invited_email')).toBe(true);
    expect(keys.has('code')).toBe(true);
    expect([...keys].filter((key) => /token|secret|password|plaintext/i.test(key))).toEqual([]);
    // And the plaintexts themselves are nowhere in it — the token is a return value only.
    const serialized = JSON.stringify(signals);
    expect(serialized).not.toContain(INVITE_TOKEN);
    expect(serialized).not.toContain(ROTATED_TOKEN);
    expect(serialized).not.toContain('evgi_');
    // No field of the store itself is named like a value.
    expect(
      Object.getOwnPropertyNames(store).filter((key) =>
        /token|secret|password|plaintext/i.test(key),
      ),
    ).toEqual([]);
  });
});
