// PolicyStore: the explicit reload a conflict dialog offers, including the retry of an open that
// failed before it could read the list.

import { TestBed } from '@angular/core/testing';
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

  describe('reload', () => {
    it('re-reads the first page and takes the new head from the answer', async () => {
      await store.open(ANCHOR_ID);
      const newHead = '018f0000-0000-7000-8000-0000000000c9';
      data.pages = [
        page([summary(ANCHOR_ID, 1, 'draft'), summary(SECOND_ID, 2, 'active')], null, newHead),
      ];

      await store.reload();

      expect(store.revisions().map((row) => row.id)).toEqual([ANCHOR_ID, SECOND_ID]);
      expect(store.headRevisionId()).toBe(newHead);
      expect(store.phase()).toBe('ready');
    });

    it('settles error for the conflict dialog’s reload when the read is refused', async () => {
      await store.open(ANCHOR_ID);
      data.listFailure = typedFailure(
        {
          error: {
            code: 'unavailable',
            message: 'The policy store is busy.',
            request_id: '018f0000-0000-7000-8000-0000000000e4',
            retryable: true,
          },
        },
        'unavailable',
        503,
      );

      await store.reload();

      expect(store.phase()).toBe('error');
      expect(store.loadError()?.code).toBe('unavailable');
    });

    it('does nothing before a revision was opened', async () => {
      await store.reload();

      expect(store.phase()).toBe('idle');
      expect(data.listCalls).toEqual([]);
    });

    it('retries the open that failed before it ever reached the list', async () => {
      // A 503 on the revision read leaves no anchor, and without one this reload used to do
      // nothing — the page's own retry would have spun on an error it could not leave.
      data.getFailure = typedFailure(
        {
          error: {
            code: 'unavailable',
            message: 'The policy store is busy.',
            request_id: '018f0000-0000-7000-8000-0000000000ec',
            retryable: true,
          },
        },
        'unavailable',
        503,
      );
      await store.open(ANCHOR_ID);

      expect(store.phase()).toBe('error');
      expect(data.listCalls).toEqual([]);

      data.getFailure = null;
      await store.reload();

      expect(store.phase()).toBe('ready');
      expect(data.getCalls).toEqual([ANCHOR_ID, ANCHOR_ID]);
      expect(data.listCalls).toEqual([{ revisionId: ANCHOR_ID, cursor: undefined }]);
      expect(store.projectId()).toBe(PROJECT_ID);
      expect(store.headRevisionId()).toBe(HEAD_ID);
    });

    it('forgets the retry id once the view is reset', async () => {
      data.getFailure = typedFailure(
        {
          error: {
            code: 'unavailable',
            message: 'The policy store is busy.',
            request_id: '018f0000-0000-7000-8000-0000000000ed',
            retryable: true,
          },
        },
        'unavailable',
        503,
      );
      await store.open(ANCHOR_ID);
      store.reset();

      data.getFailure = null;
      await store.reload();

      // Nothing is open and nothing was asked for any more: a reload after a reset is still a no-op.
      expect(store.phase()).toBe('idle');
      expect(data.getCalls).toEqual([ANCHOR_ID]);
      expect(data.listCalls).toEqual([]);
    });
  });
});
