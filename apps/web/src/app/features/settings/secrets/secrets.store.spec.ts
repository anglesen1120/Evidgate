// 09.7.1-T3 (plan 09.7.1 §2 AC3, §4 D5, §5 "secret references" row, §6 ingest-token rotation
// states, §8 S3, §10 T3.1/T3.2; COORDINATOR DECISIONS S82 P1/P3/P4/P5): the Settings > Secrets
// store.
//
// The list answers come from the real fixture corpus (`ingest-tokens-list-onboarding-token`,
// `ingest-tokens-list-onboarding-unusable`, `ingest-token-rotate`, `ingest-token-rotate-conflict`),
// so the rows, the successor and the 409 envelope are the shapes the dev backend serves and the
// contract pins. `SecretsDataService` is replaced by a fake, because this suite is about what the
// store does with an answer — the request side is `core/data/secrets-data.service.spec.ts`.
//
// Every case is a decision the tab depends on:
//
// - the rows are the server's metadata (name, scopes, prefix, status, timestamps), in server order;
// - a refused read is the tab's error state with the server's own message, and a rejection that is
//   not the server's answer leaves the typed error `null` rather than inventing copy;
// - the list follows `next_cursor` one page per call, appends, and refuses a cursor it already
//   followed or a cursor that never ends;
// - switching the project clears the rows at once and an older project's read never writes;
// - a rotation hands its one-time value back as soon as the server confirms it, re-reads the list
//   behind the caller (only the project it addressed, never awaited, never able to fail it), is
//   guarded against a double press — observably, through `rotating` — surfaces the server's 409
//   verbatim, re-reads a row that 409 proves stale, and refuses an answer with no usable secret;
// - no signal holds the rotated plaintext or a secret-shaped field name (task §8 S3, AC3): the
//   state-shape spec at the end enumerates the store's own fields and serialises every public
//   signal to prove it.

import { TestBed } from '@angular/core/testing';
import type {
  ErrorEnvelope,
  IngestTokenCreatedResponse,
  IngestTokenListResponse,
} from '@evidgate/sdk';

import { MAX_CURSOR_PAGES } from '../../../core/data/identity-data.service';
import { SecretsDataService } from '../../../core/data/secrets-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../../core/http/api-error';
import { SecretsStore } from './secrets.store';

import conflictFixture from '../../../../../../../fixtures/web-api/ingest-token-rotate-conflict/input.json';
import rotateFixture from '../../../../../../../fixtures/web-api/ingest-token-rotate/input.json';
import tokenListFixture from '../../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-token/input.json';
import unusableListFixture from '../../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-unusable/input.json';

/** The served page: one `active` token with no expiry — what a project's first token looks like. */
const PAGE = tokenListFixture.body as unknown as IngestTokenListResponse;
/** The same operation's two unusable rows: a revoked token and an expired one. */
const UNUSABLE_PAGE = unusableListFixture.body as unknown as IngestTokenListResponse;
/** The rotation's answer: the successor's metadata plus its one-time plaintext. */
const ROTATED = rotateFixture.body as unknown as IngestTokenCreatedResponse;
/** The 409 the rotate route answers for a token it will not rotate again. */
const ROTATE_CONFLICT = conflictFixture.body as unknown as ErrorEnvelope;

/**
 * The two malformed one-time values a rotate answer can carry: an empty string (the server rotated
 * but sent nothing to show) and a value that is not a string at all. Neither may reach a reveal.
 */
const MALFORMED_SECRETS: Array<[label: string, secret: unknown]> = [
  ['empty', ''],
  ['non-string', { toString: () => 'egt_malformed_secret' }],
];

/** The row at `index` of a served page, so no test reaches for `!` or `?? ''`. */
function pageRow(page: IngestTokenListResponse, index: number) {
  const row = page.items[index];
  if (row === undefined) {
    throw new Error(`the ingest-token fixture has no row ${index}`);
  }
  return row;
}

/** The row this suite rotates; named so no test reaches for `!` or `?? ''`. */
const TOKEN_ID = pageRow(PAGE, 0).id;

/** The typed failure `ApiGateway` builds from one fixture envelope. */
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

/**
 * The store's refresh is not awaited by its caller, so a test has to let its queued microtasks run
 * before it can see what the read wrote. Four ticks cover the `settledRead` hop plus the read's own
 * `await`; nothing here uses timers, so this is deterministic rather than a sleep.
 */
async function flushMicrotasks(): Promise<void> {
  for (let tick = 0; tick < 4; tick += 1) {
    await Promise.resolve();
  }
}

/**
 * The reads and the rotation the store performs. Cursor `"n"` names `pages[n]`, so a page can hand
 * out a next cursor the test controls exactly; the first page (no cursor) is `pages[0]`.
 *
 * The project's lib target is ES2022, so `Promise.withResolvers` is not available: the executor form
 * is how a deferred resolve/reject handle escapes to the test (the same spelling
 * `http-client-fetch.ts` uses).
 */
class FakeSecretsDataService {
  pages: readonly (IngestTokenListResponse | undefined)[] = [];
  /** Every list read, in order, so paging and reloads are provable. */
  readonly listCalls: Array<{ projectId: string; cursor: string | undefined }> = [];
  listFailure: Error | null = null;
  /** When true, `listFailure` fires only on a cursor page, so the first page still loads. */
  failCursorPagesOnly = false;
  /** When true, every page names a fresh cursor, so the store's page cap is the only stop. */
  endless = false;
  /** When true, a list read answers only when the test settles `pendingLists`. */
  listManual = false;
  readonly pendingLists: Array<{
    resolve: (page: IngestTokenListResponse) => void;
    reject: (error: unknown) => void;
  }> = [];
  /** Every rotation request in the order it reached the fake, so a double press is visible. */
  readonly rotateCalls: string[] = [];
  rotateFailure: Error | null = null;
  /** The answer a rotation succeeds with; a test swaps its secret to shape a malformed answer. */
  rotateAnswer: IngestTokenCreatedResponse = ROTATED;
  /** When true, a rotation answers only when the test settles `pendingRotations`. */
  rotateManual = false;
  readonly pendingRotations: Array<{
    resolve: (created: IngestTokenCreatedResponse) => void;
    reject: (error: unknown) => void;
  }> = [];

  listIngestTokens(projectId: string, cursor?: string): Promise<IngestTokenListResponse> {
    this.listCalls.push({ projectId, cursor });
    if (this.listManual) {
      return new Promise<IngestTokenListResponse>((resolve, reject) => {
        this.pendingLists.push({ resolve, reject });
      });
    }
    if (this.listFailure !== null && (!this.failCursorPagesOnly || cursor !== undefined)) {
      return Promise.reject(this.listFailure);
    }
    if (this.endless) {
      return Promise.resolve({ items: [], next_cursor: `c-${this.listCalls.length}` });
    }
    return Promise.resolve(
      this.pages[cursor === undefined ? 0 : Number(cursor)] ?? { items: [], next_cursor: null },
    );
  }

  rotateIngestToken(tokenId: string): Promise<IngestTokenCreatedResponse> {
    this.rotateCalls.push(tokenId);
    if (this.rotateManual) {
      return new Promise<IngestTokenCreatedResponse>((resolve, reject) => {
        this.pendingRotations.push({ resolve, reject });
      });
    }
    if (this.rotateFailure !== null) {
      return Promise.reject(this.rotateFailure);
    }
    return Promise.resolve(this.rotateAnswer);
  }

  /** Answers the n-th deferred list read the way the server would. */
  settleList(index: number, page: IngestTokenListResponse): void {
    const pending = this.pendingLists[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending list read ${index}`);
    }
    pending.resolve(page);
  }

  /** Settles a deferred list read with whatever the server (or the adapter) threw. */
  failList(index: number, error: unknown): void {
    const pending = this.pendingLists[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending list read ${index}`);
    }
    pending.reject(error);
  }

  /** Answers the n-th deferred rotation with the server's successor. */
  settleRotation(index: number): void {
    const pending = this.pendingRotations[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending rotation ${index}`);
    }
    pending.resolve(this.rotateAnswer);
  }

  /** Settles a deferred rotation with whatever the server (or the adapter) threw. */
  failRotation(index: number, error: unknown): void {
    const pending = this.pendingRotations[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending rotation ${index}`);
    }
    pending.reject(error);
  }
}

describe('SecretsStore (09.7.1-T3)', () => {
  let data: FakeSecretsDataService;
  let store: SecretsStore;

  beforeEach(() => {
    data = new FakeSecretsDataService();
    data.pages = [PAGE];
    TestBed.configureTestingModule({
      providers: [SecretsStore, { provide: SecretsDataService, useValue: data }],
    });
    store = TestBed.inject(SecretsStore);
  });

  describe('load', () => {
    it('starts idle and renders the project’s token metadata from the fixture page', async () => {
      expect(store.phase()).toBe('idle');
      expect(store.projectId()).toBeNull();
      expect(store.rows()).toEqual([]);

      await store.load('p-1');

      expect(store.phase()).toBe('ready');
      expect(store.projectId()).toBe('p-1');
      expect(store.rows().map((row) => row.id)).toEqual(PAGE.items.map((row) => row.id));
      expect(store.loadError()).toBeNull();
      expect(data.listCalls).toEqual([{ projectId: 'p-1', cursor: undefined }]);
    });

    it('keeps the server’s own status on a row rather than deriving a rotation state (S82 P3)', async () => {
      data.pages = [UNUSABLE_PAGE];
      await store.load('p-1');

      expect(store.rows().map((row) => row.status)).toEqual(
        UNUSABLE_PAGE.items.map((row) => row.status),
      );
      // The `expired` row carries the deadline it was judged by, as served; the `revoked` one has
      // no deadline at all. Neither becomes a status the store computed from the clock.
      const expired = store.rows().find((row) => row.status === 'expired');
      expect(expired?.expires_at).toBe('2026-10-01T00:00:00Z');
      const revoked = store.rows().find((row) => row.status === 'revoked');
      expect(revoked?.expires_at).toBeNull();
      // The wire has no `rotation_state`/`last_rotated_at`; the store invents neither.
      for (const row of store.rows()) {
        expect(row).not.toHaveProperty('rotation_state');
        expect(row).not.toHaveProperty('last_rotated_at');
      }
    });

    it('shows only the ids and metadata the server sent, no value of any kind', async () => {
      await store.load('p-1');

      expect(store.rows().map((row) => row.prefix)).toEqual(PAGE.items.map((row) => row.prefix));
      // A listed token's metadata carries a prefix, never the string the prefix came from.
      for (const row of store.rows()) {
        expect(Object.keys(row)).not.toContain('secret');
      }
    });

    it('is the tab’s error state with the server’s message when the first read is refused', async () => {
      const forbidden: ErrorEnvelope = {
        error: {
          code: 'forbidden',
          message: 'Missing permission ingest_token:read.',
          request_id: '5c9c4d0e-6a71-4b9e-9a51-2f6f1f0d3f11',
          retryable: false,
        },
      };
      data.listFailure = typedFailure(forbidden, 'forbidden', 403);

      await store.load('p-1');

      expect(store.phase()).toBe('error');
      expect(store.rows()).toEqual([]);
      expect(store.loadError()?.code).toBe('forbidden');
      expect(store.loadError()?.message).toBe(forbidden.error.message);
    });

    it('never rejects when the read is not the server’s answer, and invents no copy', async () => {
      data.listFailure = new Error('boom: token egt_should_not_leak');

      await expect(store.load('p-1')).resolves.toBeUndefined();

      expect(store.phase()).toBe('error');
      // A thrown value is not server copy, so no message is taken from it.
      expect(store.loadError()).toBeNull();
      expect(JSON.stringify([store.loadError(), store.rows()])).not.toContain('boom');
    });

    it('clears the rows the moment the scope switches, before the new read answers', async () => {
      await store.load('p-1');
      expect(store.rows().length).toBeGreaterThan(0);

      data.listManual = true;
      const switching = store.load('p-2');

      // The previous project's tokens must never render under this project's heading.
      expect(store.rows()).toEqual([]);
      expect(store.projectId()).toBe('p-2');
      expect(store.phase()).toBe('loading');
      expect(store.hasMore()).toBe(false);

      data.settleList(0, { items: [], next_cursor: null });
      await switching;
      expect(store.phase()).toBe('ready');
    });

    it('an older project’s read never writes once the scope moved on', async () => {
      data.listManual = true;
      const first = store.load('p-1');
      const second = store.load('p-2');

      data.settleList(1, { items: [pageRow(UNUSABLE_PAGE, 1)], next_cursor: null });
      await second;
      data.settleList(0, PAGE);
      await first;

      // The list on screen is p-2's, and the late p-1 answer changed nothing.
      expect(store.projectId()).toBe('p-2');
      expect(store.rows()).toHaveLength(1);
      expect(store.phase()).toBe('ready');
    });
  });

  describe('loadMore', () => {
    it('appends the next page once per call and reports when another one exists', async () => {
      const second = pageRow(UNUSABLE_PAGE, 0);
      data.pages = [
        { items: PAGE.items, next_cursor: '1' },
        { items: [second], next_cursor: null },
      ];
      await store.load('p-1');
      expect(store.hasMore()).toBe(true);

      await store.loadMore();

      expect(store.rows().map((row) => row.id)).toEqual([
        ...PAGE.items.map((row) => row.id),
        second.id,
      ]);
      expect(store.hasMore()).toBe(false);
      expect(data.listCalls).toEqual([
        { projectId: 'p-1', cursor: undefined },
        { projectId: 'p-1', cursor: '1' },
      ]);
    });

    it('does nothing when the server named no next page', async () => {
      await store.load('p-1');

      await store.loadMore();

      expect(data.listCalls).toHaveLength(1);
    });

    it('refuses a cursor it already followed instead of showing the same rows again', async () => {
      data.pages = [
        { items: PAGE.items, next_cursor: '1' },
        { items: [], next_cursor: '1' },
      ];
      await store.load('p-1');
      await store.loadMore();

      await expect(store.loadMore()).rejects.toThrow('repeated cursor "1"');
      expect(data.listCalls).toHaveLength(2);
    });

    it('stops at the page cap when the cursor never ends', async () => {
      data.endless = true;
      await store.load('p-1');

      for (let page = 1; page < MAX_CURSOR_PAGES; page += 1) {
        await store.loadMore();
      }

      await expect(store.loadMore()).rejects.toThrow(`within ${MAX_CURSOR_PAGES} pages`);
    });

    it('keeps the rows on screen and stores the refusal when a page read is refused', async () => {
      const forbidden: ErrorEnvelope = {
        error: {
          code: 'forbidden',
          message: 'Missing permission ingest_token:read.',
          request_id: '5c9c4d0e-6a71-4b9e-9a51-2f6f1f0d3f11',
          retryable: false,
        },
      };
      data.pages = [{ items: PAGE.items, next_cursor: '1' }];
      await store.load('p-1');
      const before = store.rows();
      data.listFailure = typedFailure(forbidden, 'forbidden', 403);
      data.failCursorPagesOnly = true;

      await store.loadMore();

      expect(store.rows()).toBe(before);
      expect(store.phase()).toBe('ready');
      expect(store.loadError()?.message).toBe(forbidden.error.message);
    });

    it('re-throws a cursor page failure that is not the server’s answer', async () => {
      data.pages = [{ items: PAGE.items, next_cursor: '1' }];
      await store.load('p-1');
      data.listFailure = new Error('boom');
      data.failCursorPagesOnly = true;

      await expect(store.loadMore()).rejects.toThrow('boom');
      expect(store.loadError()).toBeNull();
    });
  });

  describe('rotate', () => {
    it('hands back the successor at once and refreshes this project’s list behind the caller', async () => {
      await store.load('p-1');
      // The refresh is deferred, so the value can only be in hand if the call did not wait for it.
      data.listManual = true;

      const result = await store.rotate(TOKEN_ID);

      expect(result?.successorId).toBe(ROTATED.token.id);
      expect(result?.overlapEndsAt).toBe(ROTATED.overlap_ends_at);
      expect(result?.value).toBe(ROTATED.secret);
      expect(store.rotateState()).toEqual({ phase: 'done', tokenId: TOKEN_ID, error: null });
      expect(store.rotating()).toBe(false);
      // The refresh was still issued: a fresh first page of the same project, so the rotated row's
      // status and its successor appear without a second, wider read.
      expect(data.pendingLists).toHaveLength(1);
      expect(data.listCalls).toEqual([
        { projectId: 'p-1', cursor: undefined },
        { projectId: 'p-1', cursor: undefined },
      ]);
      expect(data.rotateCalls).toEqual([TOKEN_ID]);

      // And it lands, even though nobody awaited it.
      data.settleList(0, UNUSABLE_PAGE);
      await flushMicrotasks();
      expect(store.rows().map((row) => row.id)).toEqual(UNUSABLE_PAGE.items.map((row) => row.id));
      expect(store.phase()).toBe('ready');
    });

    it('keeps a confirmed rotation successful when the refresh behind it is refused', async () => {
      const forbidden: ErrorEnvelope = {
        error: {
          code: 'forbidden',
          message: 'Missing permission ingest_token:read.',
          request_id: '5c9c4d0e-6a71-4b9e-9a51-2f6f1f0d3f11',
          retryable: false,
        },
      };
      await store.load('p-1');
      const before = store.rows();
      // Every read from now on is the rotation's refresh, and it is deferred so the test decides
      // when (and how) it answers.
      data.listManual = true;

      const result = await store.rotate(TOKEN_ID);

      // The server minted the successor and the caller holds it; a refused list is the list's
      // problem, not the rotation's.
      expect(result?.value).toBe(ROTATED.secret);
      expect(store.rotateState()).toEqual({ phase: 'done', tokenId: TOKEN_ID, error: null });

      data.failList(0, typedFailure(forbidden, 'forbidden', 403));
      await flushMicrotasks();
      expect(store.loadError()?.code).toBe('forbidden');
      expect(store.rows()).toBe(before);
      expect(store.phase()).toBe('ready');
      expect(store.rotateState().phase).toBe('done');
    });

    it('mints one successor only: a second press while submitting is ignored', async () => {
      await store.load('p-1');
      data.rotateManual = true;

      const first = store.rotate(TOKEN_ID);
      await Promise.resolve();
      expect(store.rotateState().phase).toBe('submitting');
      const second = await store.rotate(TOKEN_ID);
      expect(second).toBeNull();
      expect(data.rotateCalls).toEqual([TOKEN_ID]);

      data.settleRotation(0);
      await expect(first).resolves.not.toBeNull();
      expect(store.rotateState().phase).toBe('done');
    });

    it.each(['invalid_transition', 'token_revoked'])(
      'surfaces the 409 %s verbatim and re-reads the row it proves stale, once',
      async (code) => {
        const revoked: ErrorEnvelope = { error: { ...ROTATE_CONFLICT.error, code } };
        await store.load('p-1');
        data.rotateFailure = typedFailure(revoked, 'conflict', 409);
        // The re-read is deferred, so the refusal is asserted before the new page can answer.
        data.listManual = true;

        const result = await store.rotate(TOKEN_ID);

        expect(result).toBeNull();
        const state = store.rotateState();
        expect(state.phase).toBe('failed');
        expect(state.tokenId).toBe(TOKEN_ID);
        expect(state.error?.status).toBe(409);
        expect(state.error?.kind).toBe('conflict');
        expect(state.error?.code).toBe(code);
        expect(state.error?.message).toBe(ROTATE_CONFLICT.error.message);
        // Exactly one re-read of this project's first page — the stale row cannot keep a Rotate
        // button that can never succeed.
        expect(data.listCalls).toEqual([
          { projectId: 'p-1', cursor: undefined },
          { projectId: 'p-1', cursor: undefined },
        ]);

        data.settleList(0, UNUSABLE_PAGE);
        await flushMicrotasks();
        expect(store.rows().map((row) => row.status)).toEqual(
          UNUSABLE_PAGE.items.map((row) => row.status),
        );
        // The refusal is still the attempt's answer: the read behind it changes no phase.
        expect(store.rotateState()).toEqual(state);
      },
    );

    it.each(MALFORMED_SECRETS)(
      'refuses an %s secret as the store’s own failure and re-reads the rotated row',
      async (_label, secret) => {
        await store.load('p-1');
        // The row did rotate server-side, so the re-read is one this attempt owes the screen —
        // deferred here so the failure state is asserted before the new page can land.
        data.listManual = true;
        data.rotateAnswer = { ...ROTATED, secret: secret as string };

        const result = await store.rotate(TOKEN_ID);

        expect(result).toBeNull();
        const state = store.rotateState();
        expect(state.phase).toBe('failed');
        expect(state.tokenId).toBe(TOKEN_ID);
        // A store-local failure shaped like every other one: no invented server code, no value.
        expect(state.error?.kind).toBe('unexpected');
        expect(state.error?.code).toBe('rotation_answer_unusable');
        expect(state.error?.retryable).toBe(false);
        expect(state.error?.requestId).toBeNull();
        expect(JSON.stringify(state)).not.toContain('egt_malformed_secret');
        // The message states the fact only: this kind renders no retry or reload control, so it
        // must not tell the user to press one (09.7.1-T3 page: controls per failure kind).
        expect(state.error?.message).not.toMatch(/reload|try again|retry/i);
        expect(store.rotating()).toBe(false);

        // The stale row is re-read once, and the refusal survives the read.
        expect(data.listCalls).toEqual([
          { projectId: 'p-1', cursor: undefined },
          { projectId: 'p-1', cursor: undefined },
        ]);
        data.settleList(0, UNUSABLE_PAGE);
        await flushMicrotasks();
        expect(store.rows().map((row) => row.status)).toEqual(
          UNUSABLE_PAGE.items.map((row) => row.status),
        );
        expect(store.rotateState()).toEqual(state);
      },
    );

    it('settles a rejection that is not the server’s answer as the generic failure', async () => {
      await store.load('p-1');
      const before = store.rows();
      const listsBefore = [...data.listCalls];
      data.rotateFailure = new Error('boom: egt_should_not_leak');

      const result = await store.rotate(TOKEN_ID);

      expect(result).toBeNull();
      const state = store.rotateState();
      expect(state.phase).toBe('failed');
      expect(state.error?.kind).toBe('unexpected');
      expect(state.error?.code).toBe('request_unresolved');
      // No key exists for this route, so a retry is not a replay.
      expect(state.error?.retryable).toBe(false);
      expect(JSON.stringify(state)).not.toContain('boom');
      // The page renders no retry or reload control for `unexpected`, so the copy names no action.
      expect(state.error?.message).not.toMatch(/reload|try again|retry/i);
      expect(store.rows()).toBe(before);
      // A cancellation or a bug proves nothing about the rows, so it re-reads nothing either.
      expect(data.listCalls).toEqual(listsBefore);
    });

    it('does nothing before a project is loaded', async () => {
      const result = await store.rotate(TOKEN_ID);

      expect(result).toBeNull();
      expect(data.rotateCalls).toEqual([]);
      expect(store.rotateState().phase).toBe('idle');
    });

    it('does not write into another project when the scope moves during the round trip', async () => {
      await store.load('p-1');
      data.rotateManual = true;
      const rotating = store.rotate(TOKEN_ID);

      await store.load('p-2');
      data.settleRotation(0);
      const result = await rotating;

      // The successor belongs to the project the user left; its dialog is gone with it.
      expect(result).toBeNull();
      expect(store.projectId()).toBe('p-2');
      expect(store.rotateState().phase).toBe('idle');
      // The only reads are the two loads: no refresh of p-1 landed on p-2's list.
      expect(data.listCalls.map((call) => call.projectId)).toEqual(['p-1', 'p-2']);
    });

    it('drops an abandoned attempt when the dialog closes mid-flight', async () => {
      await store.load('p-1');
      data.rotateManual = true;
      const rotating = store.rotate(TOKEN_ID);

      store.resetRotation();
      expect(store.rotateState().phase).toBe('idle');

      data.settleRotation(0);
      await expect(rotating).resolves.toBeNull();
      expect(store.rotateState().phase).toBe('idle');
    });

    it('keeps the in-flight lock when resetRotation() clears the published state', async () => {
      await store.load('p-1');
      data.rotateManual = true;
      const rotating = store.rotate(TOKEN_ID);

      // The dialog closed, so the published state is gone — but the request is still open.
      store.resetRotation();
      expect(store.rotateState().phase).toBe('idle');

      const second = await store.rotate(TOKEN_ID);
      expect(second).toBeNull();
      // Exactly one request reached the server for the one user action.
      expect(data.rotateCalls).toEqual([TOKEN_ID]);

      data.settleRotation(0);
      await expect(rotating).resolves.toBeNull();
      expect(store.rotateState().phase).toBe('idle');
    });

    it('reports the in-flight rotation through `rotating`, even after resetRotation()', async () => {
      expect(store.rotating()).toBe(false);
      await store.load('p-1');
      data.rotateManual = true;

      const rotating = store.rotate(TOKEN_ID);
      expect(store.rotating()).toBe(true);

      // The dialog closed, so the published attempt is gone — the request, and therefore the lock,
      // is not: the ignored press below is what L4 asks to be observable.
      store.resetRotation();
      expect(store.rotateState().phase).toBe('idle');
      expect(store.rotating()).toBe(true);
      await expect(store.rotate(TOKEN_ID)).resolves.toBeNull();
      expect(data.rotateCalls).toEqual([TOKEN_ID]);

      data.settleRotation(0);
      await expect(rotating).resolves.toBeNull();
      expect(store.rotating()).toBe(false);
    });

    it('releases the lock once the request settles, so the row can be rotated again', async () => {
      await store.load('p-1');
      data.rotateManual = true;
      const first = store.rotate(TOKEN_ID);
      data.failRotation(0, typedFailure(ROTATE_CONFLICT, 'conflict', 409));
      await first;

      data.rotateManual = false;
      const second = await store.rotate(TOKEN_ID);

      expect(second).not.toBeNull();
      expect(data.rotateCalls).toEqual([TOKEN_ID, TOKEN_ID]);
    });

    it('resetRotation() clears a finished attempt so the row can be rotated again', async () => {
      await store.load('p-1');
      await store.rotate(TOKEN_ID);
      expect(store.rotateState().phase).toBe('done');

      store.resetRotation();

      expect(store.rotateState()).toEqual({ phase: 'idle', tokenId: null, error: null });
      data.rotateFailure = typedFailure(ROTATE_CONFLICT, 'conflict', 409);
      await store.rotate(TOKEN_ID);
      expect(store.rotateState().phase).toBe('failed');
      expect(data.rotateCalls).toEqual([TOKEN_ID, TOKEN_ID]);
    });
  });

  it('holds no plaintext and no secret-shaped field after a rotation (D5, S3, AC3)', async () => {
    await store.load('p-1');
    const result = await store.rotate(TOKEN_ID);
    const value = result?.value ?? '';
    expect(value).toBe(ROTATED.secret);

    // Every public signal, serialised: the one-time value appears in none of them.
    const publicState = {
      rows: store.rows(),
      phase: store.phase(),
      loadError: store.loadError(),
      hasMore: store.hasMore(),
      projectId: store.projectId(),
      rotateState: store.rotateState(),
      rotating: store.rotating(),
    };
    const serialised = JSON.stringify(publicState);
    expect(serialised).not.toContain(value);
    expect(serialised).not.toContain('egt_rotated');

    // And no field of the store is even named like a value: metadata names only.
    const fieldNames = Object.getOwnPropertyNames(store);
    expect(fieldNames).toContain('rows');
    expect(fieldNames).toContain('rotateState');
    expect(
      fieldNames.filter((name) => /token|secret|password|plaintext|credential/i.test(name)),
    ).toEqual([]);
  });
});
