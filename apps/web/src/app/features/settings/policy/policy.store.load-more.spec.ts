// Policy history keeps one served page and opaque request cursors, never accumulated rows.

import { TestBed } from '@angular/core/testing';
import { MAX_CURSOR_PAGES } from '../../../core/data/identity-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { PolicyDataService } from '../../../core/data/policy-data.service';
import { PolicyProjectReads } from './policy-reads';
import { PolicyStore } from './policy.store';
import {
  ANCHOR_ID,
  FakePolicyDataService,
  FakeSessionStore,
  HEAD_ID,
  page,
  PROJECT_ID,
  revision,
  SECOND_ID,
  summary,
  typedFailure,
} from './policy.store.testing';

describe('PolicyStore (09.7.1-T4)', () => {
  let data: FakePolicyDataService;
  let session: FakeSessionStore;
  let store: PolicyStore;

  beforeEach(() => {
    data = new FakePolicyDataService();
    session = new FakeSessionStore();
    data.revisions.set(ANCHOR_ID, revision(ANCHOR_ID, PROJECT_ID, 2, 'active'));
    data.pages = [page([summary(ANCHOR_ID, 2, 'active')], null, HEAD_ID)];
    TestBed.configureTestingModule({
      providers: [
        PolicyStore,
        PolicyProjectReads,
        { provide: PolicyDataService, useValue: data },
        { provide: SessionStore, useValue: session },
      ],
    });
    store = TestBed.inject(PolicyStore);
  });

  describe('history paging', () => {
    it('follows the cursor once and replaces history with the served page and head', async () => {
      data.pages = [
        page([summary(ANCHOR_ID, 1, 'draft')], '1', HEAD_ID),
        page([summary(SECOND_ID, 2, 'active')], null, HEAD_ID),
      ];
      await store.open(ANCHOR_ID);
      expect(store.hasMore()).toBe(true);

      await store.loadMore();

      expect(data.listCalls).toEqual([
        { revisionId: ANCHOR_ID, cursor: undefined },
        { revisionId: ANCHOR_ID, cursor: '1' },
      ]);
      expect(store.revisions().map((row) => row.revision_no)).toEqual([2]);
      expect(store.hasMore()).toBe(false);
      expect(store.headRevisionId()).toBe(HEAD_ID);
    });

    it('refuses a cursor it already followed instead of listing the same rows again', async () => {
      data.pages = [
        page([summary(ANCHOR_ID, 1, 'active')], '1', HEAD_ID),
        // A server that hands the same cursor back a second time is a contract violation.
        page([summary(SECOND_ID, 2, 'draft')], '1', HEAD_ID),
      ];
      await store.open(ANCHOR_ID);
      await store.loadMore();

      await expect(store.loadMore()).rejects.toThrow(/repeated cursor/);
      // The refusal costs visibility of the bug, never a request.
      expect(data.listCalls).toHaveLength(2);
    });

    it('stops at the page cap when the list never ends', async () => {
      data.endless = true;
      await store.open(ANCHOR_ID);

      for (let pageRead = 1; pageRead < MAX_CURSOR_PAGES; pageRead += 1) {
        await store.loadMore();
      }

      await expect(store.loadMore()).rejects.toThrow(/did not finish within/);
      expect(data.listCalls).toHaveLength(MAX_CURSOR_PAGES);
    });

    it('keeps the rows already read when a later page is refused', async () => {
      data.pages = [page([summary(ANCHOR_ID, 1, 'active')], '1', HEAD_ID)];
      await store.open(ANCHOR_ID);
      data.listFailure = typedFailure(
        {
          error: {
            code: 'validation_failed',
            message: 'The cursor is malformed.',
            request_id: '018f0000-0000-7000-8000-0000000000e3',
            retryable: false,
          },
        },
        'validation',
        422,
      );

      await store.loadMore();

      expect(store.phase()).toBe('ready');
      expect(store.revisions().map((row) => row.id)).toEqual([ANCHOR_ID]);
      expect(store.loadError()?.code).toBe('validation_failed');
    });

    it('clears the alert a refused page raised once a later page arrives', async () => {
      // The alert belongs to the read that failed, not to the tab.
      data.pages = [
        page([summary(ANCHOR_ID, 1, 'active')], '1', HEAD_ID),
        page([summary(SECOND_ID, 2, 'draft')], null, HEAD_ID),
      ];
      await store.open(ANCHOR_ID);
      data.listFailure = typedFailure(
        {
          error: {
            code: 'unavailable',
            message: 'The policy store is busy.',
            request_id: '018f0000-0000-7000-8000-0000000000eb',
            retryable: true,
          },
        },
        'unavailable',
        503,
      );
      await store.loadMore();
      expect(store.loadError()?.code).toBe('unavailable');

      data.listFailure = null;
      await store.loadMore();

      expect(store.loadError()).toBeNull();
      expect(store.revisions().map((row) => row.id)).toEqual([SECOND_ID]);
      expect(store.phase()).toBe('ready');
    });

    it('moves first, middle, last and Previous after Next using opaque request cursors', async () => {
      data.pages = [
        page([summary(ANCHOR_ID, 1, 'retired')], '1', HEAD_ID),
        page([summary(SECOND_ID, 2, 'retired')], '2', HEAD_ID),
        page([summary(HEAD_ID, 3, 'active')], null, HEAD_ID),
      ];
      await store.open(ANCHOR_ID);
      await store.previousPage();
      expect(store.page()).toBe(1);
      expect(data.listCalls).toHaveLength(1);
      await store.loadMore();
      expect(store.page()).toBe(2);
      expect(store.hasMore()).toBe(true);
      await store.loadMore();
      expect(store.page()).toBe(3);
      expect(store.hasMore()).toBe(false);
      await store.loadMore();
      expect(data.listCalls).toHaveLength(3);
      await store.previousPage();
      expect(store.page()).toBe(2);
      expect(store.revisions().map((row) => row.id)).toEqual([SECOND_ID]);
      await store.previousPage();
      expect(store.page()).toBe(1);
      expect(data.listCalls.at(-1)?.cursor).toBeUndefined();
      await store.loadMore();
      expect(store.page()).toBe(2);
      expect(data.listCalls.at(-1)?.cursor).toBe('1');
    });

    it.each([{ rows: [] }, { rows: [summary(ANCHOR_ID, 1, 'active')] }])(
      'keeps an empty or single page at page one without navigation requests: %j',
      async ({ rows }) => {
        data.pages = [page(rows, null, HEAD_ID)];
        await store.open(ANCHOR_ID);
        await store.loadMore();
        await store.previousPage();
        expect(store.page()).toBe(1);
        expect(store.revisions()).toEqual(rows);
        expect(store.hasMore()).toBe(false);
        expect(data.listCalls).toHaveLength(1);
      },
    );

    it.each([50, 100])(
      'resets the cursor and passes selected size %i on every request',
      async (size) => {
        data.pages = [
          page([summary(ANCHOR_ID, 1, 'retired')], '1', HEAD_ID),
          page([summary(SECOND_ID, 2, 'active')], null, HEAD_ID),
        ];
        await store.open(ANCHOR_ID);
        await store.loadMore();
        await store.setPageSize(size);
        expect(store.page()).toBe(1);
        expect(store.pageSize()).toBe(size);
        expect(store.revisions().map((row) => row.id)).toEqual([ANCHOR_ID]);
        expect(data.listCalls.at(-1)?.cursor).toBeUndefined();
        await store.loadMore();
        expect(data.listLimits).toEqual([20, 20, size, size]);
        data.revisions.set(SECOND_ID, revision(SECOND_ID, PROJECT_ID, 2, 'active'));
        await store.open(SECOND_ID);
        expect(store.page()).toBe(1);
        expect(store.pageSize()).toBe(size);
        expect(data.listCalls.at(-1)).toEqual({ revisionId: SECOND_ID, cursor: undefined });
      },
    );

    it('ignores unsupported and unchanged sizes without resetting history', async () => {
      await store.open(ANCHOR_ID);
      await store.setPageSize(25);
      await store.setPageSize(20);
      expect(data.listCalls).toHaveLength(1);
      expect(store.pageSize()).toBe(20);
    });

    it('retries a failed later destination while retaining page, rows and body selection', async () => {
      data.pages = [
        page([summary(ANCHOR_ID, 1, 'active')], '1', HEAD_ID),
        page([summary(SECOND_ID, 2, 'draft')], null, HEAD_ID),
      ];
      await store.open(ANCHOR_ID);
      const selected = await store.loadBody(ANCHOR_ID);
      data.listManual = true;
      const navigation = store.loadMore();
      expect(store.paging()).toBe(true);
      expect(store.page()).toBe(1);
      expect(store.revisions().map((row) => row.id)).toEqual([ANCHOR_ID]);
      data.pendingLists[0]?.reject(
        typedFailure(
          {
            error: { code: 'unavailable', message: 'Busy', request_id: HEAD_ID, retryable: true },
          },
          'unavailable',
          503,
        ),
      );
      await navigation;
      expect(store.paging()).toBe(false);
      expect(store.page()).toBe(1);
      expect(store.revisions().map((row) => row.id)).toEqual([ANCHOR_ID]);
      data.listManual = false;
      await store.retryPage();
      expect(store.page()).toBe(2);
      expect(store.loadError()).toBeNull();
      expect(await store.loadBody(ANCHOR_ID)).toBe(selected);
      expect(data.listCalls.at(-1)?.cursor).toBe('1');
    });

    it('a newer size reset discards an older page answer', async () => {
      data.pages = [page([summary(ANCHOR_ID, 1, 'active')], '1', HEAD_ID)];
      await store.open(ANCHOR_ID);
      data.listManual = true;
      const older = store.loadMore();
      const reset = store.setPageSize(50);
      data.pendingLists[1]?.resolve(page([summary(HEAD_ID, 3, 'active')], null, HEAD_ID));
      await reset;
      data.pendingLists[0]?.resolve(page([summary(SECOND_ID, 2, 'draft')], null, HEAD_ID));
      await older;
      expect(store.page()).toBe(1);
      expect(store.pageSize()).toBe(50);
      expect(store.paging()).toBe(false);
      expect(store.revisions().map((row) => row.id)).toEqual([HEAD_ID]);
    });

    it('keeps opened revision metadata independently of its history page and clears it on reset', async () => {
      data.pages = [page([summary(SECOND_ID, 1, 'retired')], null, HEAD_ID)];
      await store.open(ANCHOR_ID);
      expect(store.openedRevision()?.id).toBe(ANCHOR_ID);
      expect(store.revisions().map((row) => row.id)).toEqual([SECOND_ID]);
      await store.setPageSize(50);
      expect(store.openedRevision()?.id).toBe(ANCHOR_ID);
      store.reset();
      expect(store.openedRevision()).toBeNull();
    });
  });
});
