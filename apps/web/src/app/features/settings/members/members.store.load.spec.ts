import type { MembersStore } from './members.store';
import {
  mountMembersStore,
  FakeMembersDataService,
  PAGE,
  EMPTY_PAGE,
  ROLES,
  LAST_ADMIN,
  INVITE_EXISTS,
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

  describe('load', () => {
    it('renders the members x roles matrix from the fixture response', async () => {
      expect(store.phase()).toBe('idle');

      await store.load('o-1');

      expect(store.phase()).toBe('ready');
      expect(store.members().map((member) => member.id)).toEqual(
        PAGE.items.map((member) => member.id),
      );
      const matrix = store.matrix();
      // Columns are the server's roles, in server order, unfiltered: the matrix is org-wide.
      expect(matrix.columns.map((role) => role.code)).toEqual(ROLES.map((role) => role.code));
      expect(matrix.rows.map((row) => row.membership.id)).toEqual(
        PAGE.items.map((member) => member.id),
      );
      // Granted cells are the role ids the server put on the membership, nothing derived.
      expect(matrix.rows.map((row) => [...row.granted])).toEqual(
        PAGE.items.map((member) => member.roles.map((role) => role.id)),
      );
      expect(store.hasMore()).toBe(false);
      expect(store.loadError()).toBeNull();
      // The tab reads one org's list, roles and catalogue, once each.
      expect(data.membershipCursors).toEqual([undefined]);
      expect(data.roleCalls).toBe(1);
      expect(data.catalogueCalls).toBe(1);
    });

    it('is ready with no rows for the empty variant, and points the matrix at no columns', async () => {
      data.pages = [EMPTY_PAGE];

      await store.load('o-1');

      expect(store.phase()).toBe('ready');
      expect(store.members()).toEqual([]);
      expect(store.matrix().rows).toEqual([]);
      expect(store.hasMore()).toBe(false);
    });

    it('is the tab error state with the server’s failure when the member read refuses', async () => {
      const failure = typedFailure(LAST_ADMIN, 'conflict', 409);
      data.membersFailure = failure;

      await store.load('o-1');

      expect(store.phase()).toBe('error');
      expect(store.loadError()).toBe(failure.error);
      expect(store.members()).toEqual([]);
      expect(store.hasMore()).toBe(false);
    });

    it('keeps the memberships when the role list fails, and says its columns are unavailable', async () => {
      data.rolesFailure = typedFailure(LAST_ADMIN, 'conflict', 409);

      await store.load('o-1');

      expect(store.phase()).toBe('ready');
      expect(store.members()).toHaveLength(PAGE.items.length);
      expect(store.roles()).toEqual([]);
      expect(store.matrix().columns).toEqual([]);
    });

    it('keeps the memberships when the catalogue fails, and the preview says why', async () => {
      data.catalogueFailure = typedFailure(LAST_ADMIN, 'conflict', 409);

      await store.load('o-1');

      expect(store.phase()).toBe('ready');
      expect(store.members()).toHaveLength(PAGE.items.length);
      expect(store.catalogue()).toBeNull();
      expect(store.preview(roleId('viewer'))).toEqual({
        kind: 'unavailable',
        reason: 'catalogue-unavailable',
      });
    });

    it('lets a newer load win: the older read writes nothing when it settles', async () => {
      data.manual = true;
      const older = store.load('o-1');
      const newer = store.load('o-2');

      data.pending[1]?.resolve(EMPTY_PAGE);
      await newer;
      data.pending[0]?.resolve(PAGE);
      await older;

      expect(store.members()).toEqual([]);
      expect(store.phase()).toBe('ready');
    });
  });
  describe('loadMore', () => {
    it('appends the next page and stops at a null cursor', async () => {
      const [first, second] = PAGE.items;
      data.pages = [
        { items: first === undefined ? [] : [first], next_cursor: '1' },
        { items: second === undefined ? [] : [second], next_cursor: null },
      ];
      await store.load('o-1');
      expect(store.hasMore()).toBe(true);

      await store.loadMore();
      expect(store.members().map((member) => member.id)).toEqual([first?.id, second?.id]);
      expect(store.hasMore()).toBe(false);

      // Nothing left to follow: a second call asks the server nothing.
      await store.loadMore();
      expect(data.membershipCursors).toEqual([undefined, '1']);
    });

    it('refuses a cursor the server hands out twice', async () => {
      data.pages = [
        { items: [], next_cursor: '1' },
        { items: [], next_cursor: '1' },
      ];
      await store.load('o-1');
      await store.loadMore();

      await expect(store.loadMore()).rejects.toThrow(/repeated cursor "1"/);
    });

    it('stops at the page cap when the cursor never ends', async () => {
      data.endless = true;
      await store.load('o-1');

      let failure: Error | null = null;
      for (let attempt = 0; attempt < 40 && failure === null; attempt += 1) {
        failure = await store.loadMore().then(
          () => null,
          (error: Error) => error,
        );
      }

      expect(failure?.message).toContain('within 20 pages');
    });

    it('keeps the rows already read when a later page refuses', async () => {
      const failure = typedFailure(INVITE_EXISTS, 'conflict', 409);
      data.pages = [{ items: PAGE.items, next_cursor: '1' }];
      data.failCursorPagesOnly = true;
      data.membersFailure = failure;
      await store.load('o-1');

      await store.loadMore();

      expect(store.members()).toHaveLength(PAGE.items.length);
      expect(store.phase()).toBe('ready');
      expect(store.loadError()).toBe(failure.error);
      expect(store.hasMore()).toBe(true);
    });

    it('re-throws a rejection that is not the server’s answer, for the page to catch', async () => {
      const failure = new Error('adapter bug: the page never arrived');
      data.pages = [{ items: PAGE.items, next_cursor: '1' }];
      data.failCursorPagesOnly = true;
      data.membersFailure = failure;
      await store.load('o-1');

      // Not a typed refusal: a bug, re-thrown unchanged rather than read as a server decision.
      await expect(store.loadMore()).rejects.toBe(failure);
      expect(store.members()).toHaveLength(PAGE.items.length);
      expect(store.loadError()).toBeNull();
    });
  });
  describe('memberById', () => {
    it('answers the row the list holds, and nothing for an id it does not', async () => {
      await store.load('o-1');

      expect(store.memberById(pageMember(1).id)).toBe(store.members()[1]);
      expect(store.memberById('')).toBeUndefined();
      expect(store.memberById('m-unknown')).toBeUndefined();
    });
  });
});
