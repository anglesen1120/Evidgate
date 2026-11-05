// PolicyStore: opening a revision, the list it reads through that id, and the refusals that must
// not reveal whether another project's revision exists.

import { TestBed } from '@angular/core/testing';
import type { ApiErrorKind } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { PolicyDataService } from '../../../core/data/policy-data.service';
import { PolicyProjectReads } from './policy-reads';
import { PolicyStore } from './policy.store';
import {
  ACTIVATION,
  ANCHOR_ID,
  FakePolicyDataService,
  FakeSessionStore,
  flushMicrotasks,
  HEAD_ID,
  OTHER_PROJECT_ID,
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

  describe('open', () => {
    it('reads the revision, takes its project, and lists that project through it', async () => {
      expect(store.phase()).toBe('idle');
      expect(store.projectId()).toBeNull();

      await store.open(ANCHOR_ID);

      expect(store.phase()).toBe('ready');
      expect(data.getCalls).toEqual([ANCHOR_ID]);
      // The list route takes a revision id too, so the opened id is the anchor of its own list.
      expect(data.listCalls).toEqual([{ revisionId: ANCHOR_ID, cursor: undefined }]);
      expect(store.projectId()).toBe(PROJECT_ID);
      expect(store.revisions().map((row) => row.id)).toEqual([ANCHOR_ID]);
      expect(store.headRevisionId()).toBe(HEAD_ID);
      expect(store.hasMore()).toBe(false);
      expect(store.loadError()).toBeNull();
    });

    it('requests nothing at all for an id that is not UUID-shaped', async () => {
      await store.open('not-a-revision-id');

      expect(store.phase()).toBe('idle');
      expect(data.getCalls).toEqual([]);
      expect(data.listCalls).toEqual([]);
      expect(data.activationCalls).toEqual([]);
      expect(data.headCalls).toEqual([]);
    });

    it('reads the project activation and head beside the list, through the seam', async () => {
      await store.open(ANCHOR_ID);
      await flushMicrotasks();

      expect(data.activationCalls).toEqual([PROJECT_ID]);
      expect(data.headCalls).toEqual([PROJECT_ID]);
      expect(store.reads.activation().phase).toBe('ready');
      expect(store.activation()).toEqual(ACTIVATION);
      expect(store.mode()).toBe('advisory');
      expect(store.reads.head().value?.revision_no).toBe(3);
    });

    it('keeps the served list when both project reads fail', async () => {
      data.activation = Promise.reject(
        typedFailure(
          {
            error: {
              code: 'rate_limited',
              message: 'the read burst limit for this project was reached',
              request_id: '018f0000-0000-7000-8000-00000000a10e',
              retryable: true,
            },
          },
          'rate_limited',
          429,
        ),
      );
      data.head = Promise.reject(
        typedFailure(
          {
            error: {
              code: 'unavailable',
              message: 'a dependency is busy',
              request_id: '018f0000-0000-7000-8000-00000000a10f',
              retryable: true,
            },
          },
          'unavailable',
          503,
        ),
      );

      await store.open(ANCHOR_ID);
      await flushMicrotasks();

      expect(store.phase()).toBe('ready');
      expect(store.revisions().map((row) => row.id)).toEqual([ANCHOR_ID]);
      expect(store.reads.activation().phase).toBe('error');
      expect(store.reads.head().phase).toBe('error');
      // A refused activation is not a mode: the chip falls back to "unavailable", never a guess.
      expect(store.activation()).toBeNull();
      expect(store.mode()).toBeNull();
      // The list's own alert stays for the list: a failed summary read is the summary's state.
      expect(store.loadError()).toBeNull();
    });

    it('re-reads the project activation and head on reload', async () => {
      await store.open(ANCHOR_ID);

      await store.reload();

      expect(data.activationCalls).toEqual([PROJECT_ID, PROJECT_ID]);
      expect(data.headCalls).toEqual([PROJECT_ID, PROJECT_ID]);
    });

    it('clears the project reads with the view', async () => {
      await store.open(ANCHOR_ID);

      store.reset();

      expect(store.reads.activation().phase).toBe('idle');
      expect(store.reads.head().phase).toBe('idle');
      expect(store.activation()).toBeNull();
    });

    it('refuses a revision of a project this session is not in, with no list read', async () => {
      data.revisions.set(ANCHOR_ID, revision(ANCHOR_ID, OTHER_PROJECT_ID, 2, 'draft'));

      await store.open(ANCHOR_ID);

      // The same state a 404 renders: the tab cannot tell the two apart, so it cannot probe.
      expect(store.phase()).toBe('not_found');
      expect(store.projectId()).toBeNull();
      expect(store.revisions()).toEqual([]);
      expect(store.loadError()).toBeNull();
      expect(data.listCalls).toEqual([]);
    });

    it.each<{ status: number; kind: ApiErrorKind }>([
      { status: 404, kind: 'not_found' },
      { status: 403, kind: 'forbidden' },
    ])('renders a $status $kind refusal as not_found', async ({ status, kind }) => {
      data.getFailure = typedFailure(
        {
          error: {
            code: kind,
            message: 'The policy revision was not found.',
            request_id: '018f0000-0000-7000-8000-0000000000e1',
            retryable: false,
          },
        },
        kind,
        status,
      );

      await store.open(ANCHOR_ID);

      expect(store.phase()).toBe('not_found');
      expect(store.loadError()).toBeNull();
      expect(data.listCalls).toEqual([]);
    });

    it('renders a refused antiforgery token as error, not as a missing revision', async () => {
      // `csrf_failed` says nothing about whether the revision exists: hiding it behind
      // `not_found` would tell the user "there is nothing here" about a fault a reload can fix.
      data.getFailure = typedFailure(
        {
          error: {
            code: 'csrf_failed',
            message: 'The antiforgery token was refused.',
            request_id: '018f0000-0000-7000-8000-0000000000e9',
            retryable: true,
          },
        },
        'csrf_failed',
        403,
      );

      await store.open(ANCHOR_ID);

      expect(store.phase()).toBe('error');
      expect(store.loadError()?.code).toBe('csrf_failed');
      expect(store.loadError()?.message).toBe('The antiforgery token was refused.');
      expect(data.listCalls).toEqual([]);
    });

    it('keeps the server’s own failure for a refusal that is not a not-found', async () => {
      data.getFailure = typedFailure(
        {
          error: {
            code: 'unavailable',
            message: 'The policy store is busy.',
            request_id: '018f0000-0000-7000-8000-0000000000e2',
            retryable: true,
          },
        },
        'unavailable',
        503,
      );

      await store.open(ANCHOR_ID);

      expect(store.phase()).toBe('error');
      expect(store.loadError()?.code).toBe('unavailable');
      expect(store.loadError()?.message).toBe('The policy store is busy.');
      expect(data.listCalls).toEqual([]);
    });

    it('never rejects when the rejection is not the server’s answer', async () => {
      data.getFailure = new Error('boom: the caller must not see this');

      await expect(store.open(ANCHOR_ID)).resolves.toBeUndefined();

      expect(store.phase()).toBe('error');
      expect(store.loadError()).toBeNull();
      expect(JSON.stringify([store.loadError(), store.revisions()])).not.toContain('boom');
    });

    it('clears the previous revision’s view while the new one loads', async () => {
      await store.open(ANCHOR_ID);
      expect(store.revisions().length).toBe(1);

      data.getManual = true;
      const switching = store.open(SECOND_ID);

      expect(store.phase()).toBe('loading');
      expect(store.revisions()).toEqual([]);
      expect(store.headRevisionId()).toBeNull();
      expect(store.projectId()).toBeNull();

      data.settleGet(0, revision(SECOND_ID, PROJECT_ID, 3, 'draft'));
      await switching;
      expect(store.phase()).toBe('ready');
    });

    it('ignores an older open that answers after a newer one', async () => {
      session.rows = [{ id: PROJECT_ID }, { id: OTHER_PROJECT_ID }];
      data.getManual = true;

      const first = store.open(ANCHOR_ID);
      const second = store.open(SECOND_ID);

      // The first answer arrives last: the generation guard means it writes nothing at all.
      data.settleGet(1, revision(SECOND_ID, OTHER_PROJECT_ID, 3, 'draft'));
      await second;
      data.settleGet(0, revision(ANCHOR_ID, PROJECT_ID, 2, 'active'));
      await first;

      expect(store.projectId()).toBe(OTHER_PROJECT_ID);
      expect(store.revisions().map((row) => row.id)).toEqual([ANCHOR_ID]);
      expect(store.phase()).toBe('ready');
    });
  });
});
