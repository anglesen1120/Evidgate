import type { RoleView } from '@evidgate/sdk';
import type { MembersStore } from './members.store';
import {
  mountMembersStore,
  FakeMembersDataService,
  ROLES,
  roleId,
} from './members.store.test-support';

describe('MembersStore (09.7.1-T2)', () => {
  let data: FakeMembersDataService;
  let store: MembersStore;

  beforeEach(() => {
    ({ data, store } = mountMembersStore());
  });

  describe('preview', () => {
    it('previews a built-in role from the live catalogue, flags sensitive grants', async () => {
      await store.load('o-1');

      const preview = store.preview(roleId('qa_engineer'));

      expect(preview.kind).toBe('ready');
      if (preview.kind !== 'ready') {
        return;
      }
      const artifact = preview.groups.find((group) => group.resource === 'artifact');
      expect(artifact?.permissions.map((entry) => entry.code)).toEqual([
        'artifact:read',
        'artifact:read_sensitive',
        'artifact:write',
      ]);
      expect(artifact?.permissions.map((entry) => entry.sensitive)).toEqual([false, true, false]);
      // qa_engineer grants 23 codes; the served page holds nine of them, so fourteen are counted
      // as omitted rather than rendered without a description.
      expect(preview.omitted).toBe(14);
    });

    it('answers unknown-role for an id the store does not hold', async () => {
      await store.load('o-1');

      expect(store.preview('')).toEqual({ kind: 'unavailable', reason: 'unknown-role' });
      expect(store.preview('role-does-not-exist')).toEqual({
        kind: 'unavailable',
        reason: 'unknown-role',
      });
    });

    it('shows the org’s own role as unavailable rather than as an empty grant list', async () => {
      const custom: RoleView = {
        id: 'role-auditor',
        org_id: 'o-1',
        code: 'auditor',
        name: 'Auditor',
        is_builtin: false,
        created_at: '2026-10-02T09:00:00Z',
        version: 1,
      };
      data.roles = [...ROLES, custom];
      await store.load('o-1');

      expect(store.preview(custom.id)).toEqual({ kind: 'unavailable', reason: 'custom-role' });
    });
  });
});
