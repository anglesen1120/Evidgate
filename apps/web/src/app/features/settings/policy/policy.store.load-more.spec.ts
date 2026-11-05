// PolicyStore: following the list cursor one page per call, and the contract violations that throw
// rather than pass a partial list off as complete.

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

  describe('loadMore', () => {
    it('follows the cursor once and keeps the head the answer named', async () => {
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
      expect(store.revisions().map((row) => row.revision_no)).toEqual([1, 2]);
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
      expect(store.revisions().map((row) => row.id)).toEqual([ANCHOR_ID, SECOND_ID]);
      expect(store.phase()).toBe('ready');
    });
  });
});
