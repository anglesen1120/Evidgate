// 09.7.1-T4 (plan 09.7.1 §2 AC1/AC4/AC5, §4, §8 S3, §10 T4; COORDINATOR DECISIONS S83 P1–P4):
// the Settings > Policy store.
//
// `PolicyDataService` is replaced by a fake, because this suite is about what the store does with an
// answer — the request side is `core/data/policy-data.service.spec.ts`. Every case is a decision the
// tab depends on:
//
// - a revision id is the key: it is UUID-checked before any request, the PROJECT comes from the
//   revision itself, and a revision of a project this session is not in is refused as `not_found`
//   with nothing else requested — the tab cannot be used to probe for another project's revisions;
// - the head is the list answer's `ETag` (never an optimistic value), and it is what a publish
//   quotes in `If-Match`;
// - the list follows `next_cursor` one page per call and refuses a cursor it already followed;
// - a publication's local refusals (no `policy:write`, not a JSON object, a COMPACT body over
//   65 536 bytes — never the pretty-printed text) send nothing at all, a `412` answers `conflict`
//   with the head the server named and is never retried, and every other refusal — `422` included —
//   is handed back verbatim;
// - the caller gets the stored revision before the list is re-read, and `activeRevision` moves only
//   when a list answer says which row is `active`; a refresh that fails keeps the rows and surfaces
//   its own `ApiError` through the page's alert, and a `loadMore` that succeeds clears what an
//   earlier one left there;
// - `csrf_failed` is an error with the server's copy, never "there is nothing here" — only the two
//   existence-hiding refusals become `not_found`;
// - concurrent `loadBody` calls for one revision share one read, and a read that settles after a
//   reset answers its caller without filling a cache that belongs to a view that is gone;
// - `reload` retries the open that failed before the list, from the id that open was asked for;
// - the mode chip renders exactly `policy_activation.mode`: the four mappings are proved here
//   through `applyActivation`, and nothing is derived from a revision's `state`.

import { TestBed } from '@angular/core/testing';
import type {
  ErrorEnvelope,
  PolicyActivationDto,
  PolicyRevisionDto,
  PolicyRevisionSummaryDto,
} from '@evidgate/sdk';

import { MAX_CURSOR_PAGES } from '../../../core/data/identity-data.service';
import {
  PolicyDataService,
  type PolicyRevisionPage,
  type PublishedRevision,
} from '../../../core/data/policy-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { policyModeView } from './policy-mode';
import { PolicyStore } from './policy.store';

const PROJECT_ID = '018f0000-0000-7000-8000-00000000p001';
const OTHER_PROJECT_ID = '018f0000-0000-7000-8000-00000000p002';
/** The revision the page opens; the list route is asked about this id. */
const ANCHOR_ID = '018f0000-0000-7000-8000-0000000000a1';
/** The head the list answer's `ETag` names: the value a publish must quote. */
const HEAD_ID = '018f0000-0000-7000-8000-0000000000b2';
/** A second revision, so `activeRevision` has something to move away from. */
const SECOND_ID = '018f0000-0000-7000-8000-0000000000b3';

/** One list row, as the list route serves them (no body). */
function summary(
  id: string,
  revisionNo: number,
  state: PolicyRevisionSummaryDto['state'],
): PolicyRevisionSummaryDto {
  return {
    id,
    revision_no: revisionNo,
    hash: `sha256:${revisionNo}`,
    state,
    created_by: 'u-1',
    created_at: '2026-10-20T06:05:00Z',
    version: 1,
  };
}

/** One stored revision, as `GET /v1/policies/{id}` serves it. */
function revision(
  id: string,
  projectId: string,
  revisionNo: number,
  state: PolicyRevisionDto['state'],
): PolicyRevisionDto {
  return { ...summary(id, revisionNo, state), project_id: projectId, body: { flaky_budget: 1 } };
}

/** One served page: rows, the cursor after them, and the head the answer's `ETag` named. */
function page(
  items: readonly PolicyRevisionSummaryDto[],
  nextCursor: string | null,
  headRevisionId: string | null,
): PolicyRevisionPage {
  return { items, nextCursor, headRevisionId };
}

/**
 * A JSON body of exactly `bytes` UTF-8 bytes. `{"a":"` is six bytes and `"}` is two, so the filler
 * carries the rest — which is how the 65 536/65 537 boundary is proved byte-exactly.
 */
function bodyOfSize(bytes: number): string {
  return `{"a":"${'x'.repeat(bytes - 8)}"}`;
}

/** The typed failure `ApiGateway` builds from one envelope. */
function typedFailure(
  envelope: ErrorEnvelope,
  kind: ApiErrorKind,
  status: number,
): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: envelope.error.code,
    message: envelope.error.message,
    requestId: envelope.error.request_id,
    retryable: envelope.error.retryable,
    details: envelope.error.details ?? [],
    retryAfterSeconds: null,
  });
}

/** One deferred answer, so a test controls exactly when a read settles. */
type Deferred<T> = {
  readonly resolve: (value: T) => void;
  readonly reject: (error: unknown) => void;
};

/**
 * The reads the store performs. Cursor `"n"` names `pages[n]`, so a page can hand out a next cursor
 * the test controls exactly; the first page (no cursor) is `pages[0]`.
 *
 * The project's lib target is ES2022, so `Promise.withResolvers` is not available: the executor form
 * is how a deferred resolve/reject handle escapes to the test (the same spelling
 * `http-client-fetch.ts` uses).
 */
class FakePolicyDataService {
  readonly revisions = new Map<string, PolicyRevisionDto>();
  pages: readonly PolicyRevisionPage[] = [];
  readonly getCalls: string[] = [];
  readonly listCalls: Array<{ revisionId: string; cursor: string | undefined }> = [];
  readonly publishCalls: Array<{ projectId: string; body: unknown; ifMatch: string | null }> = [];
  getFailure: Error | null = null;
  listFailure: Error | null = null;
  publishFailure: Error | null = null;
  publishAnswer: PublishedRevision | null = null;
  /** When true, every page names a fresh cursor, so the store's page cap is the only stop. */
  endless = false;
  getManual = false;
  listManual = false;
  publishManual = false;
  readonly pendingGets: Deferred<PolicyRevisionDto>[] = [];
  readonly pendingLists: Deferred<PolicyRevisionPage>[] = [];
  readonly pendingPublishes: Deferred<PublishedRevision>[] = [];

  getRevision(revisionId: string): Promise<PolicyRevisionDto> {
    this.getCalls.push(revisionId);
    if (this.getManual) {
      return new Promise<PolicyRevisionDto>((resolve, reject) => {
        this.pendingGets.push({ resolve, reject });
      });
    }
    if (this.getFailure !== null) {
      return Promise.reject(this.getFailure);
    }
    const stored = this.revisions.get(revisionId);
    return stored === undefined
      ? Promise.reject(new Error(`the fake holds no revision ${revisionId}`))
      : Promise.resolve(stored);
  }

  listRevisions(revisionId: string, cursor?: string): Promise<PolicyRevisionPage> {
    this.listCalls.push({ revisionId, cursor });
    if (this.listManual) {
      return new Promise<PolicyRevisionPage>((resolve, reject) => {
        this.pendingLists.push({ resolve, reject });
      });
    }
    if (this.listFailure !== null) {
      return Promise.reject(this.listFailure);
    }
    if (this.endless) {
      return Promise.resolve({
        items: [],
        nextCursor: `c-${this.listCalls.length}`,
        headRevisionId: HEAD_ID,
      });
    }
    return Promise.resolve(
      this.pages[cursor === undefined ? 0 : Number(cursor)] ?? page([], null, null),
    );
  }

  publish(projectId: string, body: unknown, ifMatch: string | null): Promise<PublishedRevision> {
    this.publishCalls.push({ projectId, body, ifMatch });
    if (this.publishManual) {
      return new Promise<PublishedRevision>((resolve, reject) => {
        this.pendingPublishes.push({ resolve, reject });
      });
    }
    if (this.publishFailure !== null) {
      return Promise.reject(this.publishFailure);
    }
    if (this.publishAnswer === null) {
      return Promise.reject(new Error('the fake holds no publish answer'));
    }
    return Promise.resolve(this.publishAnswer);
  }

  /** Answers the n-th deferred revision read the way the server would. */
  settleGet(index: number, stored: PolicyRevisionDto): void {
    const pending = this.pendingGets[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending revision read ${index}`);
    }
    pending.resolve(stored);
  }

  /** Settles a deferred revision read with whatever the server (or the adapter) threw. */
  failGet(index: number, error: unknown): void {
    const pending = this.pendingGets[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending revision read ${index}`);
    }
    pending.reject(error);
  }

  /** Answers the n-th deferred list read the way the server would. */
  settleList(index: number, served: PolicyRevisionPage): void {
    const pending = this.pendingLists[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending list read ${index}`);
    }
    pending.resolve(served);
  }

  /** Settles a deferred list read with whatever the server (or the adapter) threw. */
  failList(index: number, error: unknown): void {
    const pending = this.pendingLists[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending list read ${index}`);
    }
    pending.reject(error);
  }

  /** Answers the n-th deferred publish with the server's stored revision. */
  settlePublish(index: number, stored: PublishedRevision): void {
    const pending = this.pendingPublishes[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending publish ${index}`);
    }
    pending.resolve(stored);
  }
}

/**
 * The store's session dependencies here: which projects the caller is in, and the permission codes
 * it holds. `policy:read` is what the tab itself needs; `policy:write` is what a publish needs, and it
 * is present by default so a case about publishing does not have to say so.
 */
class FakeSessionStore {
  rows: readonly { readonly id: string }[] = [{ id: PROJECT_ID }];
  codes: ReadonlySet<string> = new Set(['policy:read', 'policy:write']);

  projects(): readonly { readonly id: string }[] {
    return this.rows;
  }

  has(code: string): boolean {
    return this.codes.has(code);
  }
}

/**
 * The publish's refresh and every other background write are not awaited by their caller, so a test
 * has to let the queued microtasks run before it can see what the read wrote. Four ticks cover the
 * `settledRead` hop plus the read's own `await`; nothing here uses timers, so this is deterministic.
 */
async function flushMicrotasks(): Promise<void> {
  for (let tick = 0; tick < 4; tick += 1) {
    await Promise.resolve();
  }
}

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
      // `csrf_failed` says nothing about whether the revision exists (F2): hiding it behind
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
      // F7: the alert belongs to the read that failed, not to the tab.
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
      // F6: a 503 on the revision read leaves no anchor, and without one this reload used to do
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

  describe('loadBody', () => {
    it('reads a body once and serves the cached one after that', async () => {
      data.revisions.set(ANCHOR_ID, revision(ANCHOR_ID, PROJECT_ID, 2, 'active'));

      const first = await store.loadBody(ANCHOR_ID);
      const second = await store.loadBody(ANCHOR_ID);

      expect(second).toBe(first);
      expect(data.getCalls).toEqual([ANCHOR_ID]);
    });

    it('keeps at most eight bodies, dropping the oldest read', async () => {
      for (let index = 0; index < 9; index += 1) {
        const id = `018f0000-0000-7000-8000-0000000000d${index}`;
        data.revisions.set(id, revision(id, PROJECT_ID, index + 1, 'draft'));
        await store.loadBody(id);
      }

      // The ninth read is cached; the first one was dropped, so asking for it reads again.
      const newest = '018f0000-0000-7000-8000-0000000000d8';
      const oldest = '018f0000-0000-7000-8000-0000000000d0';
      const readsBefore = data.getCalls.length;
      await store.loadBody(newest);
      expect(data.getCalls).toHaveLength(readsBefore);
      await store.loadBody(oldest);
      expect(data.getCalls).toHaveLength(readsBefore + 1);
    });

    it('rejects with the server’s failure, which both callers render', async () => {
      data.getFailure = typedFailure(
        {
          error: {
            code: 'not_found',
            message: 'The policy revision was not found.',
            request_id: '018f0000-0000-7000-8000-0000000000e5',
            retryable: false,
          },
        },
        'not_found',
        404,
      );

      await expect(store.loadBody(ANCHOR_ID)).rejects.toMatchObject({
        error: { kind: 'not_found' },
      });
    });

    it('shares one read between concurrent calls for the same revision', async () => {
      // The diff and the publish dialog ask in the same tick (F3); one immutable revision is one GET.
      data.getManual = true;

      const diff = store.loadBody(ANCHOR_ID);
      const dialog = store.loadBody(ANCHOR_ID);
      expect(data.getCalls).toEqual([ANCHOR_ID]);

      const stored = revision(ANCHOR_ID, PROJECT_ID, 2, 'active');
      data.settleGet(0, stored);

      await expect(diff).resolves.toBe(stored);
      await expect(dialog).resolves.toBe(stored);
      expect(data.getCalls).toEqual([ANCHOR_ID]);
      // The shared answer is the cached one, so a later call costs no request either.
      await expect(store.loadBody(ANCHOR_ID)).resolves.toBe(stored);
      expect(data.getCalls).toEqual([ANCHOR_ID]);
    });

    it('lets a later call start a new read after the shared one was refused', async () => {
      data.getManual = true;
      const first = store.loadBody(ANCHOR_ID);
      const joined = store.loadBody(ANCHOR_ID);

      const refusal = typedFailure(
        {
          error: {
            code: 'unavailable',
            message: 'The policy store is busy.',
            request_id: '018f0000-0000-7000-8000-0000000000ee',
            retryable: true,
          },
        },
        'unavailable',
        503,
      );
      data.failGet(0, refusal);

      await expect(first).rejects.toBe(refusal);
      await expect(joined).rejects.toBe(refusal);
      // The in-flight entry is gone, so a retry is a real retry rather than the same rejection.
      data.getManual = false;
      const retried = await store.loadBody(ANCHOR_ID);
      expect(retried.id).toBe(ANCHOR_ID);
      expect(data.getCalls).toEqual([ANCHOR_ID, ANCHOR_ID]);
    });

    it('answers its caller but caches nothing when a reset lands mid-read', async () => {
      const stored = revision(ANCHOR_ID, PROJECT_ID, 2, 'active');
      data.revisions.set(ANCHOR_ID, stored);
      data.getManual = true;
      const reading = store.loadBody(ANCHOR_ID);
      store.reset();

      data.settleGet(0, stored);

      await expect(reading).resolves.toBe(stored);
      // The body belonged to a view that is gone, so the cache stays empty and the next call reads.
      data.getManual = false;
      await expect(store.loadBody(ANCHOR_ID)).resolves.toBe(stored);
      expect(data.getCalls).toEqual([ANCHOR_ID, ANCHOR_ID]);
    });
  });

  describe('applyActivation', () => {
    function activation(mode: PolicyActivationDto['mode']): PolicyActivationDto {
      return {
        project_id: PROJECT_ID,
        policy_revision_id: ANCHOR_ID,
        hash: 'sha256:2',
        mode,
        activated_by: 'u-1',
        activated_at: '2026-10-20T06:05:00Z',
        previous_policy_revision_id: null,
        changed: true,
      };
    }

    it.each([
      { label: 'enforcing', mode: 'enforcing' as const, kind: 'gating' },
      { label: 'advisory', mode: 'advisory' as const, kind: 'advisory' },
      // The wire type narrows the mode, but a server-declared third value must never be rounded
      // into one of the two the chip knows: it is shown as unknown with its own text.
      { label: 'an undeclared mode', mode: 'permissive' as unknown as 'advisory', kind: 'unknown' },
    ])('maps $label through applyActivation to the $kind chip', ({ mode, kind }) => {
      store.applyActivation(activation(mode));

      expect(store.mode()).toBe(mode);
      expect(policyModeView(store.mode()).kind).toBe(kind);
    });

    it('keeps the raw undeclared value so the chip can show the server’s own text', () => {
      store.applyActivation(activation('permissive' as unknown as 'advisory'));

      expect(policyModeView(store.mode())).toEqual({ kind: 'unknown', raw: 'permissive' });
    });

    it('answers no mode at all when no activation was applied', () => {
      expect(store.activation()).toBeNull();
      expect(store.mode()).toBeNull();
      expect(policyModeView(store.mode())).toEqual({ kind: 'unavailable' });
    });

    it('clears the activation when another revision is opened', async () => {
      store.applyActivation(activation('enforcing'));
      await store.open(ANCHOR_ID);

      expect(store.activation()).toBeNull();
      expect(policyModeView(store.mode())).toEqual({ kind: 'unavailable' });
    });
  });

  describe('publish', () => {
    beforeEach(async () => {
      data.publishAnswer = { revision: revision(SECOND_ID, PROJECT_ID, 3, 'draft'), created: true };
      await store.open(ANCHOR_ID);
    });

    it('quotes the last list answer’s ETag in If-Match', async () => {
      const result = await store.publish(bodyOfSize(64));

      expect(data.publishCalls).toEqual([
        { projectId: PROJECT_ID, body: { a: 'x'.repeat(56) }, ifMatch: HEAD_ID },
      ]);
      expect(result.kind).toBe('created');
      if (result.kind === 'created' || result.kind === 'unchanged') {
        expect(result.revision.id).toBe(SECOND_ID);
      }
    });

    it('reads a 200 answer as unchanged', async () => {
      data.publishAnswer = {
        revision: revision(ANCHOR_ID, PROJECT_ID, 2, 'active'),
        created: false,
      };

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('unchanged');
    });

    it('sends a compact body of exactly 65 536 bytes and refuses 65 537 without a request', async () => {
      const atCap = await store.publish(bodyOfSize(65_536));

      expect(atCap.kind).toBe('created');
      expect(data.publishCalls).toHaveLength(1);

      const overCap = await store.publish(bodyOfSize(65_537));

      expect(overCap).toEqual({ kind: 'too_large', bytes: 65_537 });
      expect(data.publishCalls).toHaveLength(1);
    });

    it('measures the compact body, so a pretty-printed one under the cap is still sent', async () => {
      // The editor seeds itself with the two-space indented document (F1), and indentation is a large
      // part of that text: `critical_cases` with 16 000 members is 64 020 bytes compact and more than
      // twice the cap once every member sits on its own indented line.
      const compact = `{"critical_cases":[${new Array<string>(16_000).fill('"a"').join(',')}]}`;
      const pretty = JSON.stringify(JSON.parse(compact), null, 2);
      expect(new TextEncoder().encode(compact).byteLength).toBe(64_020);
      expect(new TextEncoder().encode(pretty).byteLength).toBeGreaterThan(2 * 65_536);

      const result = await store.publish(pretty);

      expect(result.kind).toBe('created');
      // What travels is the parsed object, so the bytes on the wire are the compact ones.
      expect(data.publishCalls[0]?.body).toEqual(JSON.parse(compact));
      expect(data.publishCalls).toHaveLength(1);
    });

    it('refuses a pretty-printed body whose compact form is over the cap, quoting that size', async () => {
      const compact = `{"critical_cases":[${new Array<string>(16_400).fill('"a"').join(',')}]}`;
      const compactBytes = new TextEncoder().encode(compact).byteLength;
      expect(compactBytes).toBe(65_620);

      const result = await store.publish(JSON.stringify(JSON.parse(compact), null, 2));

      // The refusal reports the size the cap is about — the wire body, not the text on screen.
      expect(result).toEqual({ kind: 'too_large', bytes: compactBytes });
      expect(data.publishCalls).toEqual([]);
    });

    it('counts a multi-byte character as every byte it occupies', async () => {
      // `{"note":"` is nine bytes and `"}` is two, and "ế" is three UTF-8 bytes: 21 842 of them are
      // 65 526 bytes, so the compact body is 65 537 bytes while the TEXT is only 21 853 characters
      // long. A character-counted cap would let this through — the cap is declared in bytes, so it is
      // measured in bytes.
      const overCap = `{"note":"${'ế'.repeat(21_842)}"}`;
      expect(overCap.length).toBe(21_853);
      expect(new TextEncoder().encode(overCap).byteLength).toBe(65_537);

      const refused = await store.publish(overCap);

      expect(refused).toEqual({ kind: 'too_large', bytes: 65_537 });
      expect(data.publishCalls).toEqual([]);

      // Two ASCII characters instead of the last three-byte one is exactly 65 536 bytes: accepted.
      const atCap = `{"note":"${'ế'.repeat(21_841)}xx"}`;
      expect(new TextEncoder().encode(atCap).byteLength).toBe(65_536);

      const sent = await store.publish(atCap);

      expect(sent.kind).toBe('created');
      expect(data.publishCalls).toHaveLength(1);
    });

    it.each([
      ['text that is not JSON', 'not json at all'],
      ['a JSON array', '[{"a":1}]'],
      ['a JSON scalar', '42'],
      ['JSON null', 'null'],
    ])('refuses %s as invalid_json without sending anything', async (_label, bodyText) => {
      const result = await store.publish(bodyText);

      expect(result).toEqual({ kind: 'invalid_json' });
      expect(data.publishCalls).toEqual([]);
    });

    it('answers busy for a second call while one is still in flight', async () => {
      data.publishManual = true;

      const first = store.publish(bodyOfSize(64));
      const second = await store.publish(bodyOfSize(64));

      expect(second).toEqual({ kind: 'busy' });
      expect(store.publishing()).toBe(true);
      expect(data.publishCalls).toHaveLength(1);

      data.settlePublish(0, {
        revision: revision(SECOND_ID, PROJECT_ID, 3, 'draft'),
        created: true,
      });
      await expect(first).resolves.toMatchObject({ kind: 'created' });
      expect(store.publishing()).toBe(false);
    });

    it('answers conflict with the head the server named on a 412, and never retries', async () => {
      const currentHead = '018f0000-0000-7000-8000-0000000000c1';
      const conflict = typedFailure(
        {
          error: {
            code: 'precondition_failed',
            message: "the project's policy head changed since it was read",
            request_id: '018f0000-0000-7000-8000-0000000000e6',
            retryable: false,
            details: [
              { path: 'If-Match', reason: 'stale' },
              { path: 'revision_id', reason: currentHead },
            ],
          },
        },
        'precondition_failed',
        412,
      );
      data.publishFailure = conflict;

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('conflict');
      if (result.kind === 'conflict') {
        expect(result.error).toBe(conflict.error);
        expect(result.currentRevisionId).toBe(currentHead);
      }
      // One attempt: a 412 is the end of it, and the reload is the user's explicit choice.
      expect(data.publishCalls).toHaveLength(1);
      // The head on screen is still the one the list answer named, not the server's new one.
      expect(store.headRevisionId()).toBe(HEAD_ID);
    });

    it('carries no current head when the server refused a malformed If-Match', async () => {
      data.publishFailure = typedFailure(
        {
          error: {
            code: 'precondition_failed',
            message: 'If-Match must be the ETag this endpoint issued: a revision id (00 §9.1)',
            request_id: '018f0000-0000-7000-8000-0000000000e7',
            retryable: false,
            details: [{ path: 'If-Match', reason: 'invalid' }],
          },
        },
        'precondition_failed',
        412,
      );

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('conflict');
      if (result.kind === 'conflict') {
        expect(result.currentRevisionId).toBeNull();
      }
    });

    it('hands a 422 back verbatim, with its code, message and field details', async () => {
      const refusal = typedFailure(
        {
          error: {
            code: 'policy_unknown_field',
            message: 'The policy body has a member the schema does not declare.',
            request_id: '018f0000-0000-7000-8000-0000000000e8',
            retryable: false,
            details: [{ path: '/notes', reason: 'additional_properties' }],
          },
        },
        'validation',
        422,
      );
      data.publishFailure = refusal;

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('refused');
      if (result.kind === 'refused') {
        // The same object the gateway built: the tab renders the server's own code and details.
        expect(result.error).toBe(refusal.error);
        expect(result.error.code).toBe('policy_unknown_field');
        expect(result.error.details).toEqual([{ path: '/notes', reason: 'additional_properties' }]);
      }
    });

    it('surfaces a transport failure as refused with its ApiError and never retries', async () => {
      const network = new ApiRequestError({
        kind: 'network',
        status: 0,
        code: 'network',
        message: 'The request did not reach the server.',
        requestId: null,
        retryable: true,
        details: [],
        retryAfterSeconds: null,
      });
      data.publishFailure = network;

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('refused');
      if (result.kind === 'refused') {
        expect(result.error).toBe(network.error);
      }
      expect(data.publishCalls).toHaveLength(1);
    });

    it('answers refused with its own copy for a non-server rejection', async () => {
      data.publishFailure = new Error('boom: the caller must not see this');

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('refused');
      if (result.kind === 'refused') {
        expect(result.error.code).toBe('policy_publish_unresolved');
        expect(result.error.message).not.toContain('boom');
      }
      expect(store.publishing()).toBe(false);
    });

    it('returns the answer before the refresh moves activeRevision', async () => {
      // The opened page names ANCHOR_ID as the active revision; a publish does not change that
      // until a list answer says which row is active.
      expect(store.activeRevision()?.id).toBe(ANCHOR_ID);
      data.listManual = true;

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('created');
      expect(store.activeRevision()?.id).toBe(ANCHOR_ID);
      // The refresh is already out — it is just not awaited by the caller.
      expect(data.pendingLists).toHaveLength(1);
      expect(data.listCalls).toHaveLength(2);

      const newHead = '018f0000-0000-7000-8000-0000000000ca';
      data.settleList(0, page([summary(SECOND_ID, 3, 'active')], null, newHead));
      await flushMicrotasks();

      expect(store.activeRevision()?.id).toBe(SECOND_ID);
      expect(store.headRevisionId()).toBe(newHead);
    });

    it('keeps the stored revision when the refresh behind it fails', async () => {
      data.listManual = true;
      const result = await store.publish(bodyOfSize(64));
      data.failList(0, new Error('boom: the refresh broke'));
      await flushMicrotasks();

      expect(result.kind).toBe('created');
      expect(store.phase()).toBe('ready');
      expect(store.loadError()).toBeNull();
      expect(store.headRevisionId()).toBe(HEAD_ID);
    });

    it('surfaces a refused refresh through the alert without losing the ready list', async () => {
      // F4: the head the screen shows is now stale, so the refusal is not nobody's — but the stored
      // revision and every row stay, because they are still true.
      data.listManual = true;
      const result = await store.publish(bodyOfSize(64));
      data.failList(
        0,
        typedFailure(
          {
            error: {
              code: 'unavailable',
              message: 'The policy store is busy.',
              request_id: '018f0000-0000-7000-8000-0000000000ea',
              retryable: true,
            },
          },
          'unavailable',
          503,
        ),
      );
      await flushMicrotasks();

      expect(result.kind).toBe('created');
      expect(store.loadError()?.code).toBe('unavailable');
      expect(store.phase()).toBe('ready');
      expect(store.revisions().map((row) => row.id)).toEqual([ANCHOR_ID]);
      expect(store.headRevisionId()).toBe(HEAD_ID);

      // The next read that succeeds clears it, so the alert does not outlive the failure.
      data.listManual = false;
      data.pages = [page([summary(SECOND_ID, 3, 'active')], null, SECOND_ID)];
      await store.reload();

      expect(store.loadError()).toBeNull();
      expect(store.headRevisionId()).toBe(SECOND_ID);
    });

    it('refuses without sending when this session does not hold policy:write', async () => {
      // F5: the control is absent from the DOM without the code, so this is the rule repeated where
      // the request is built — nothing is sent, and no request is made to learn what is known.
      session.codes = new Set(['policy:read']);

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('refused');
      if (result.kind === 'refused') {
        expect(result.error.code).toBe('policy_write_forbidden');
        expect(result.error).toEqual(
          expect.objectContaining({ kind: 'forbidden', status: 0, retryable: false }),
        );
      }
      expect(data.publishCalls).toEqual([]);
      expect(store.publishing()).toBe(false);
    });

    it('answers busy before it looks at the permission, so one press is one outcome', async () => {
      data.publishManual = true;
      const first = store.publish(bodyOfSize(64));

      session.codes = new Set(['policy:read']);
      const second = await store.publish(bodyOfSize(64));

      expect(second).toEqual({ kind: 'busy' });
      data.settlePublish(0, {
        revision: revision(SECOND_ID, PROJECT_ID, 3, 'draft'),
        created: true,
      });
      await expect(first).resolves.toMatchObject({ kind: 'created' });
    });

    it('refuses without sending when no revision is open', async () => {
      store.reset();

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('refused');
      if (result.kind === 'refused') {
        expect(result.error.code).toBe('policy_scope_unavailable');
      }
      expect(data.publishCalls).toEqual([]);
    });
  });

  describe('reset', () => {
    it('clears the view, returns to idle and abandons a read in flight', async () => {
      data.getManual = true;
      const opening = store.open(ANCHOR_ID);
      store.reset();

      data.settleGet(0, revision(ANCHOR_ID, PROJECT_ID, 2, 'active'));
      await opening;

      expect(store.phase()).toBe('idle');
      expect(store.projectId()).toBeNull();
      expect(store.revisions()).toEqual([]);
      expect(store.headRevisionId()).toBeNull();
      expect(store.loadError()).toBeNull();
      expect(store.activation()).toBeNull();
    });
  });
});
