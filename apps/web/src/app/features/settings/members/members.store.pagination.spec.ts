import type { MembersStore } from './members.store';
import { EMPTY_PAGE, PAGE, SELF_ESCALATION, mountMembersStore, pageMember, typedFailure, type FakeMembersDataService } from './members.store.test-support';

describe('MembersStore cursor pages', () => {
  let store: MembersStore;
  let data: FakeMembersDataService;
  beforeEach(() => {
    ({ store, data } = mountMembersStore());
    data.pages = [
      { items: [pageMember(0)], next_cursor: '1' },
      { items: [pageMember(1)], next_cursor: '2' },
      { items: [pageMember(2)], next_cursor: null },
    ];
  });

  it('shows first, middle and last pages without accumulating rows, then goes back', async () => {
    await store.load('org');
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(20);
    expect(store.members()).toEqual([pageMember(0)]);
    await store.loadMore();
    expect(store.page()).toBe(2);
    expect(store.hasMore()).toBe(true);
    expect(store.members()).toEqual([pageMember(1)]);
    await store.loadMore();
    expect(store.page()).toBe(3);
    expect(store.hasMore()).toBe(false);
    expect(store.members()).toEqual([pageMember(2)]);
    await store.previous();
    expect(store.page()).toBe(2);
    expect(store.members()).toEqual([pageMember(1)]);
    await store.previous();
    expect(store.page()).toBe(1);
    expect(store.members()).toEqual([pageMember(0)]);
    await store.loadMore();
    expect(store.page()).toBe(2);
    expect(data.membershipCursors).toEqual([undefined, '1', '2', '1', undefined, '1']);
    expect(data.membershipLimits).toEqual([20, 20, 20, 20, 20, 20]);
    expect(data.roleCalls).toBe(1);
  });

  it.each([EMPTY_PAGE, PAGE])('does not request another page for a complete response', async (page) => {
    data.pages = [page];
    await store.load('org');
    await store.previous();
    await store.loadMore();
    expect(store.page()).toBe(1);
    expect(store.members()).toEqual(page.items);
    expect(store.hasMore()).toBe(false);
    expect(data.membershipCalls).toBe(1);
  });

  it.each([50, 100, 20])('resets cursor history and requests selected size %s', async (size) => {
    await store.load('org');
    await store.loadMore();
    if (size === 20) await store.setPageSize(50);
    await store.setPageSize(size);
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(size);
    expect(data.membershipCursors.at(-1)).toBeUndefined();
    expect(data.membershipLimits.at(-1)).toBe(size);
    await store.previous();
    expect(store.page()).toBe(1);
  });

  it('ignores unsupported and unchanged sizes without resetting a page', async () => {
    await store.load('org');
    await store.loadMore();
    await store.setPageSize(25);
    await store.setPageSize(20);
    expect(store.page()).toBe(2);
    expect(data.membershipCalls).toBe(2);
  });

  it('retains the committed page on a transient failure and retries the same cursor', async () => {
    await store.load('org');
    data.membersFailure = typedFailure(SELF_ESCALATION, 'unavailable', 503);
    data.failCursorPagesOnly = true;
    await store.loadMore();
    expect(store.page()).toBe(1);
    expect(store.members()).toEqual([pageMember(0)]);
    expect(store.phase()).toBe('ready');
    expect(store.loadError()?.kind).toBe('unavailable');
    expect(store.busy()).toBe(false);
    data.membersFailure = null;
    await store.loadMore();
    expect(store.page()).toBe(2);
    expect(store.loadError()).toBeNull();
    expect(data.membershipCursors).toEqual([undefined, '1', '1']);
  });

  it('retains the current page when Previous fails and retries Previous', async () => {
    await store.load('org');
    await store.loadMore();
    data.membersFailure = typedFailure(SELF_ESCALATION, 'unavailable', 503);
    await store.previous();
    expect(store.page()).toBe(2);
    expect(store.members()).toEqual([pageMember(1)]);
    data.membersFailure = null;
    await store.previous();
    expect(store.page()).toBe(1);
    expect(store.loadError()).toBeNull();
  });

  it.each(['forbidden', 'unauthenticated', 'not_found'] as const)('clears visible data when navigation is refused as %s', async (kind) => {
    await store.load('org');
    data.membersFailure = typedFailure(SELF_ESCALATION, kind, kind === 'forbidden' ? 403 : kind === 'unauthenticated' ? 401 : 404);
    data.failCursorPagesOnly = true;
    await store.loadMore();
    expect(store.members()).toEqual([]);
    expect(store.phase()).toBe('error');
    expect(store.loadError()?.kind).toBe(kind);
    expect(store.hasMore()).toBe(false);
  });
  it('discards a late next-page success after a size change', async () => {
    await store.load('org');
    data.manual = true;
    const old = store.loadMore();
    expect(store.busy()).toBe(true);
    const replacement = store.setPageSize(50);
    data.pending[1]?.resolve({ items: [pageMember(3)], next_cursor: null });
    await replacement;
    data.pending[0]?.resolve({ items: [pageMember(1)], next_cursor: '2' });
    await old;
    expect(store.members()).toEqual([pageMember(3)]);
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(50);
    expect(store.busy()).toBe(false);
  });

  it('discards an old page after a scope reload and prevents simultaneous navigation', async () => {
    await store.load('org');
    data.manual = true;
    const old = store.loadMore();
    await store.loadMore();
    await store.previous();
    expect(data.pending).toHaveLength(1);
    const fresh = store.load('other-org');
    data.pending[1]?.resolve(EMPTY_PAGE);
    await fresh;
    data.pending[0]?.resolve(PAGE);
    await old;
    expect(store.members()).toEqual([]);
    expect(store.page()).toBe(1);
    expect(store.hasMore()).toBe(false);
  });
  it('discards a stale failed navigation after a new scope has loaded', async () => {
    await store.load('org');
    data.manual = true;
    const old = store.loadMore();
    const fresh = store.load('other-org');
    data.pending[1]?.resolve(EMPTY_PAGE);
    await fresh;
    data.pending[0]?.reject(typedFailure(SELF_ESCALATION, 'forbidden', 403));
    await old;
    expect(store.members()).toEqual([]);
    expect(store.phase()).toBe('ready');
    expect(store.loadError()).toBeNull();
    expect(store.page()).toBe(1);
    expect(store.busy()).toBe(false);
  });
});
