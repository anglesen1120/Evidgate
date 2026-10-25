// 09.7.1-F15 (plan 09.7.1 §2 AC3, §4 `.../settings/secrets/` row — `SecretsStore` holds
// `ingestTokens` and `ciBindings`, metadata rows only — §8 S3; COORDINATOR DECISIONS S86 P1–P5):
// the Secrets store's CI-binding track, which is the section beside the token list.
//
// The pages come from the real fixture corpus (`ci-bindings-list-settings`,
// `ci-bindings-list-settings-empty`, `ci-bindings-list-settings-forbidden`), so the rows are the
// shape the dev backend serves and `contracts/openapi.yaml` pins. `SecretsDataService` is replaced
// by a fake, because this suite is about what the store does with an answer — the request side is
// `core/data/secrets-data.service.spec.ts`.
//
// Every case is a decision the section depends on:
//
// - the rows are the server's metadata (`CiBindingView`, verbatim), in server order, and the token
//   list's own state is not touched by a binding read (the two tracks are independent routes);
// - an empty page is its own `empty` phase, not an error and not "loading forever";
// - a 403 is the `forbidden` phase — the section is a page state — and every other typed refusal
//   (429, 503, …) is `error` carrying the server's own failure, `Retry-After` included;
// - a rejection that is not the server's answer is `unavailable` with NO typed error: the store
//   invents no copy the server never sent;
// - the list follows `next_cursor` one page per call, appends by `id` (a row the list already holds
//   is dropped, never rendered twice), sends no request once the page named no further cursor
//   (`null`, omitted or empty), and refuses a cursor it already followed or one that never ends;
// - switching the project clears the rows at once and an older project's read never writes, not
//   even its rows;
// - `resetCiBindings()` drops the whole track and abandons a read in flight; nothing is read on
//   construction, because loading happens only through `loadCiBindings(projectId)`;
// - no signal holds a secret-shaped field or value (task §8 S3, AC3): the state-shape spec at the
//   end enumerates the store's own fields and serialises every public signal to prove it, and the
//   listed row carries no challenge — a list never does (`CiBindingView`'s own contract text).

import { TestBed } from '@angular/core/testing';
import type {
  CiBindingListResponse,
  CiBindingView,
  ErrorEnvelope,
  IngestTokenListResponse,
  IngestTokenView,
} from '@evidgate/sdk';

import { MAX_CURSOR_PAGES } from '../../../core/data/identity-data.service';
import { SecretsDataService } from '../../../core/data/secrets-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../../core/http/api-error';
import { SecretsStore } from './secrets.store';

import emptyFixture from '../../../../../../../fixtures/web-api/ci-bindings-list-settings-empty/input.json';
import forbiddenFixture from '../../../../../../../fixtures/web-api/ci-bindings-list-settings-forbidden/input.json';
import settingsFixture from '../../../../../../../fixtures/web-api/ci-bindings-list-settings/input.json';

/** The section's page: five bindings covering every verification state on `Storefront web`. */
const PAGE = settingsFixture.body as unknown as CiBindingListResponse;
/** The same read's empty page, as the corpus serves it: a project whose CI is not bound yet. */
const EMPTY_PAGE = emptyFixture.body as unknown as CiBindingListResponse;
/** The 403 envelope of the same route. */
const FORBIDDEN = forbiddenFixture.body as unknown as ErrorEnvelope;

/** How many rows a full page of the route holds: the contract's maximum `limit` (00 §9.1). */
const FULL_PAGE_ROWS = 200;

/**
 * One ingest-token metadata row, seeded into the fake's token read so "a binding read never
 * rewrites the token list" is observable. Metadata only — `IngestTokenView` carries no value.
 */
const TOKEN_ROW: IngestTokenView = {
  id: 't-1',
  project_id: 'p-1',
  name: 'Release CI',
  prefix: 'egt_ab12',
  status: 'active',
  scopes: ['event:write', 'run:create'],
  expires_at: null,
  overlap_ends_at: null,
  last_used_at: null,
  rotated_to_id: null,
  created_at: '2026-10-20T06:05:00Z',
};

/** The row at `index` of the served page, so no test reaches for `!` or `?? ''`. */
function pageRow(page: CiBindingListResponse, index: number): CiBindingView {
  const row = page.items[index];
  if (row === undefined) {
    throw new Error(`the ci-binding fixture has no row ${index}`);
  }
  return row;
}

/**
 * A page of `count` generated rows, `label` keeping ids unique across pages that are appended to
 * each other. Only the id and the two fields a page assertion reads are distinctive.
 */
function makeRows(count: number, label: string): CiBindingView[] {
  return Array.from({ length: count }, (_unused, index) => ({
    ...pageRow(PAGE, 0),
    id: `00000000-0000-7000-8000-${label}${String(index).padStart(6, '0')}`,
    repository_full_name: `acme/${label}-${index}`,
  }));
}

/** The typed failure `ApiGateway` builds from one fixture envelope. */
function typedFailure(
  envelope: ErrorEnvelope,
  kind: ApiErrorKind,
  status: number,
  retryAfterSeconds: number | null = null,
): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: envelope.error.code,
    message: envelope.error.message,
    requestId: envelope.error.request_id,
    retryable: envelope.error.retryable,
    details: envelope.error.details ?? [],
    retryAfterSeconds,
  });
}

/** The envelope of a refusal that is not the served 403: rate limit and unavailable. */
function refusal(code: string, message: string): ErrorEnvelope {
  return {
    error: {
      code,
      message,
      request_id: '018f0000-0000-7000-8000-0000000000d5',
      retryable: true,
      details: [],
    },
  };
}

/**
 * The reads the store performs, both tracks, so a test can prove a binding read neither calls the
 * token route nor is called by it. Cursor `"n"` names `pages[n]`, so a page can hand out a next
 * cursor the test controls exactly; the first page (no cursor) is `pages[0]`.
 *
 * The project's lib target is ES2022, so `Promise.withResolvers` is not available: the executor form
 * is how a deferred resolve/reject handle escapes to the test.
 */
class FakeSecretsDataService {
  pages: readonly (CiBindingListResponse | undefined)[] = [];
  /** Every binding read, in order, so paging, reloads and resets are provable. */
  readonly listCalls: Array<{ projectId: string; cursor: string | undefined }> = [];
  listFailure: Error | null = null;
  /** When true, a binding read answers only when the test settles `pendingLists`. */
  listManual = false;
  readonly pendingLists: Array<{
    resolve: (page: CiBindingListResponse) => void;
    reject: (error: unknown) => void;
  }> = [];
  /** Every token-list read, so "a binding read does not touch the token track" is observable. */
  readonly tokenListCalls: string[] = [];
  /** The token page a token read answers; empty unless a test seeds one. */
  tokenPage: IngestTokenListResponse = { items: [], next_cursor: null };

  listCiBindings(projectId: string, cursor?: string): Promise<CiBindingListResponse> {
    this.listCalls.push({ projectId, cursor });
    if (this.listManual) {
      return new Promise<CiBindingListResponse>((resolve, reject) => {
        this.pendingLists.push({ resolve, reject });
      });
    }
    if (this.listFailure !== null) {
      return Promise.reject(this.listFailure);
    }
    return Promise.resolve(this.pages[cursor === undefined ? 0 : Number(cursor)] ?? EMPTY_PAGE);
  }

  /** The token route: this suite only needs it countable, and seedable when a test needs a row. */
  listIngestTokens(projectId: string): Promise<IngestTokenListResponse> {
    this.tokenListCalls.push(projectId);
    return Promise.resolve(this.tokenPage);
  }

  /** Answers the n-th deferred binding read the way the server would. */
  settleList(index: number, page: CiBindingListResponse): void {
    const pending = this.pendingLists[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending binding read ${index}`);
    }
    pending.resolve(page);
  }

  /** Settles a deferred binding read with whatever the server (or the adapter) threw. */
  failList(index: number, error: unknown): void {
    const pending = this.pendingLists[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending binding read ${index}`);
    }
    pending.reject(error);
  }
}

describe('SecretsStore ci-bindings (09.7.1-F15)', () => {
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

  it('starts idle, reads nothing on construction, and renders the served metadata verbatim', async () => {
    // Loading happens only through loadCiBindings(): a store that fetched on construction would
    // request a project the page has not gated on `project:read` yet (S86 P3).
    expect(store.ciBindingsPhase()).toBe('idle');
    expect(store.ciBindings()).toEqual([]);
    expect(store.ciBindingsProjectId()).toBeNull();
    expect(store.ciBindingsError()).toBeNull();
    expect(store.hasMoreCiBindings()).toBe(false);
    expect(data.listCalls).toEqual([]);

    await store.loadCiBindings('p-1');

    expect(store.ciBindingsPhase()).toBe('ready');
    expect(store.ciBindingsProjectId()).toBe('p-1');
    // The SDK type verbatim: the rows the server served, in server order, with nothing derived.
    expect(store.ciBindings()).toEqual(PAGE.items);
    expect(store.ciBindingsError()).toBeNull();
    expect(store.hasMoreCiBindings()).toBe(false);
    expect(data.listCalls).toEqual([{ projectId: 'p-1', cursor: undefined }]);
    // The token track is the other half of the tab and is not read — nor written — by this one.
    expect(data.tokenListCalls).toEqual([]);
    expect(store.phase()).toBe('idle');
  });

  it('shows the three verification states the page serves, each on its own row', async () => {
    await store.loadCiBindings('p-1');

    expect(new Set(store.ciBindings().map((row) => row.status))).toEqual(
      new Set<CiBindingView['status']>(['active', 'pending_verification', 'disabled']),
    );
    const pending = store.ciBindings().find((row) => row.status === 'pending_verification');
    expect(pending?.activated_at).toBeNull();
  });

  it('is `empty` — not an error — when the project has no binding at all', async () => {
    data.pages = [EMPTY_PAGE];

    await store.loadCiBindings('p-1');

    expect(store.ciBindingsPhase()).toBe('empty');
    expect(store.ciBindings()).toEqual([]);
    expect(store.ciBindingsError()).toBeNull();
    expect(store.hasMoreCiBindings()).toBe(false);
  });

  it('is the `forbidden` phase when the server refuses the read with a 403', async () => {
    data.listFailure = typedFailure(FORBIDDEN, 'forbidden', 403);

    await store.loadCiBindings('p-1');

    // A section the caller may not read is a page state, not an error message.
    expect(store.ciBindingsPhase()).toBe('forbidden');
    expect(store.ciBindings()).toEqual([]);
    expect(store.ciBindingsError()?.kind).toBe('forbidden');
    expect(store.ciBindingsError()?.code).toBe(FORBIDDEN.error.code);
    expect(store.ciBindingsError()?.message).toBe(FORBIDDEN.error.message);
    expect(store.hasMoreCiBindings()).toBe(false);
  });

  it('is `error` with the server’s own failure and retry delay on a 429', async () => {
    const limited = refusal('rate_limited', 'too many binding reads in this window');
    data.listFailure = typedFailure(limited, 'rate_limited', 429, 30);

    await store.loadCiBindings('p-1');

    expect(store.ciBindingsPhase()).toBe('error');
    expect(store.ciBindingsError()?.kind).toBe('rate_limited');
    expect(store.ciBindingsError()?.status).toBe(429);
    expect(store.ciBindingsError()?.code).toBe('rate_limited');
    // The contract's bounded delay travels with the failure, so the page can count down to it.
    expect(store.ciBindingsError()?.retryAfterSeconds).toBe(30);
    expect(store.ciBindings()).toEqual([]);
  });

  it('is `error` on a 503, carrying the typed unavailable failure it was sent', async () => {
    const busy = refusal('dependency_unavailable', 'a dependency is temporarily unavailable');
    data.listFailure = typedFailure(busy, 'unavailable', 503, 5);

    await store.loadCiBindings('p-1');

    expect(store.ciBindingsPhase()).toBe('error');
    expect(store.ciBindingsError()?.kind).toBe('unavailable');
    expect(store.ciBindingsError()?.status).toBe(503);
    expect(store.ciBindingsError()?.message).toBe(busy.error.message);
  });

  it('is `error` — not `forbidden` — on a 403 antiforgery refusal the session may retry', async () => {
    // The phase follows the typed KIND, not the status: `csrf_failed` is a 403 that names a token
    // the page can re-read and retry, so it must not render as "you may not see this".
    data.listFailure = typedFailure(
      refusal('csrf_failed', 'the antiforgery token did not match'),
      'csrf_failed',
      403,
    );

    await store.loadCiBindings('p-1');

    expect(store.ciBindingsPhase()).toBe('error');
    expect(store.ciBindingsError()?.kind).toBe('csrf_failed');
    expect(store.ciBindingsError()?.status).toBe(403);
  });

  it('is `unavailable` with no typed error when the rejection is not the server’s answer', async () => {
    // A transport failure the mapper never saw, a cancellation, a bug: nothing here is server copy,
    // so the store reports the generic state and takes no message from the thrown value.
    data.listFailure = new Error('boom: https://example.test/ingest and a trailing value');

    await expect(store.loadCiBindings('p-1')).resolves.toBeUndefined();

    expect(store.ciBindingsPhase()).toBe('unavailable');
    expect(store.ciBindingsError()).toBeNull();
    expect(JSON.stringify([store.ciBindingsError(), store.ciBindings()])).not.toContain('boom');
  });

  it('appends the next page by following one cursor, without dropping a row it already shows', async () => {
    const firstPage = makeRows(2, 'p1');
    const third = pageRow(PAGE, 0);
    data.pages = [
      { items: firstPage, next_cursor: '1' },
      { items: [third], next_cursor: null },
    ];

    await store.loadCiBindings('p-1');
    expect(store.hasMoreCiBindings()).toBe(true);
    expect(store.ciBindings()).toHaveLength(2);

    await store.loadMoreCiBindings();

    expect(store.ciBindings().map((row) => row.id)).toEqual([
      ...firstPage.map((row) => row.id),
      third.id,
    ]);
    expect(store.ciBindingsPhase()).toBe('ready');
    expect(store.hasMoreCiBindings()).toBe(false);
    expect(data.listCalls).toEqual([
      { projectId: 'p-1', cursor: undefined },
      { projectId: 'p-1', cursor: '1' },
    ]);
  });

  it('never renders the same binding twice when a later page repeats a row', async () => {
    const [first, second, third] = [pageRow(PAGE, 0), pageRow(PAGE, 1), pageRow(PAGE, 2)];
    data.pages = [
      { items: [first, second], next_cursor: '1' },
      // A write between the two reads shifted the keyset window: the server repeats `second`.
      { items: [second, third], next_cursor: null },
    ];

    await store.loadCiBindings('p-1');
    await store.loadMoreCiBindings();

    expect(store.ciBindings().map((row) => row.id)).toEqual([first.id, second.id, third.id]);
  });

  it('offers the next page on a full 200-row page that names a cursor, and sends no request after it', async () => {
    const fullPage = makeRows(FULL_PAGE_ROWS, 'full');
    data.pages = [
      { items: fullPage, next_cursor: '1' },
      { items: [], next_cursor: null },
    ];

    await store.loadCiBindings('p-1');

    // The page size is the contract's maximum, and a cursor beside it means there IS more.
    expect(store.ciBindings()).toHaveLength(FULL_PAGE_ROWS);
    expect(store.hasMoreCiBindings()).toBe(true);

    await store.loadMoreCiBindings();

    expect(data.listCalls).toHaveLength(2);
    // The follow-up page was empty and named no cursor: the list now ends, with every row kept.
    expect(store.hasMoreCiBindings()).toBe(false);
    expect(store.ciBindings()).toHaveLength(FULL_PAGE_ROWS);
  });

  it('offers no next page on a full 200-row page that names none, and sends no request', async () => {
    data.pages = [{ items: makeRows(FULL_PAGE_ROWS, 'last'), next_cursor: null }];

    await store.loadCiBindings('p-1');
    await store.loadMoreCiBindings();

    expect(store.ciBindings()).toHaveLength(FULL_PAGE_ROWS);
    expect(store.hasMoreCiBindings()).toBe(false);
    // One read: no project or no cursor is not an error and not a request (P4).
    expect(data.listCalls).toEqual([{ projectId: 'p-1', cursor: undefined }]);
  });

  it('treats an omitted or empty next_cursor as the last page and sends no request', async () => {
    const omitted = { items: [pageRow(PAGE, 0)] } as unknown as CiBindingListResponse;
    const blank: CiBindingListResponse = { items: [pageRow(PAGE, 1)], next_cursor: '' };

    // First the page that OMITS `next_cursor` entirely: the store must read that as the last page.
    data.pages = [omitted];
    await store.loadCiBindings('p-1');
    expect(store.hasMoreCiBindings()).toBe(false);
    await store.loadMoreCiBindings();
    expect(data.listCalls).toHaveLength(1);

    // Then the page that names an EMPTY STRING, which is not a cursor any server can follow. A
    // cursor-less read always serves `pages[0]`, so the fake has to be handed this page by itself
    // for the `''` branch of the read to be what the assertions exercise.
    data.pages = [blank];
    await store.loadCiBindings('p-2');
    expect(store.ciBindings().map((row) => row.id)).toEqual([pageRow(PAGE, 1).id]);
    expect(store.hasMoreCiBindings()).toBe(false);
    await store.loadMoreCiBindings();
    // Still two reads: the `''` page had no follow-up and the store asked for none.
    expect(data.listCalls).toHaveLength(2);
    expect(data.listCalls.map((call) => call.projectId)).toEqual(['p-1', 'p-2']);
  });

  it('refuses a cursor it already followed instead of looping on the same page', async () => {
    data.pages = [
      { items: [pageRow(PAGE, 0)], next_cursor: '1' },
      { items: [], next_cursor: '1' },
    ];

    await store.loadCiBindings('p-1');
    await store.loadMoreCiBindings();

    await expect(store.loadMoreCiBindings()).rejects.toThrow('repeated cursor "1"');
    expect(data.listCalls).toHaveLength(2);
  });

  it('stops at the page cap when the cursor never ends', async () => {
    let page = 0;
    data.pages = Array.from({ length: MAX_CURSOR_PAGES + 1 }, () => {
      page += 1;
      return { items: [], next_cursor: String(page) };
    });

    await store.loadCiBindings('p-1');
    for (let read = 1; read < MAX_CURSOR_PAGES; read += 1) {
      await store.loadMoreCiBindings();
    }

    await expect(store.loadMoreCiBindings()).rejects.toThrow(`within ${MAX_CURSOR_PAGES} pages`);
  });

  it('keeps the rows on screen and stores the refusal when a follow-up page is refused', async () => {
    data.pages = [{ items: PAGE.items, next_cursor: '1' }];
    await store.loadCiBindings('p-1');
    data.listFailure = typedFailure(refusal('forbidden', 'revoked mid-session'), 'forbidden', 403);

    await store.loadMoreCiBindings();

    expect(store.ciBindings()).toEqual(PAGE.items);
    expect(store.ciBindingsPhase()).toBe('ready');
    expect(store.ciBindingsError()?.kind).toBe('forbidden');
  });

  it('clears the rows the moment the scope switches, before the new read answers', async () => {
    await store.loadCiBindings('p-1');
    expect(store.ciBindings().length).toBeGreaterThan(0);

    data.listManual = true;
    const switching = store.loadCiBindings('p-2');

    // The previous project's bindings must never render under this project's heading.
    expect(store.ciBindings()).toEqual([]);
    expect(store.ciBindingsProjectId()).toBe('p-2');
    expect(store.ciBindingsPhase()).toBe('loading');
    expect(store.hasMoreCiBindings()).toBe(false);

    data.settleList(0, EMPTY_PAGE);
    await switching;
    expect(store.ciBindingsPhase()).toBe('empty');
  });

  it('an older project’s read never writes once the scope moved on — not its rows either', async () => {
    data.listManual = true;
    const first = store.loadCiBindings('p-1');
    const second = store.loadCiBindings('p-2');

    data.settleList(1, { items: [pageRow(PAGE, 4)], next_cursor: null });
    await second;
    data.settleList(0, PAGE);
    await first;

    // The list on screen is p-2's, and the late p-1 answer changed nothing: a stale page must not
    // be appended to the new project's rows.
    expect(store.ciBindingsProjectId()).toBe('p-2');
    expect(store.ciBindings()).toHaveLength(1);
    expect(store.ciBindings()[0]?.id).toBe(pageRow(PAGE, 4).id);
    expect(store.ciBindingsPhase()).toBe('ready');
  });

  it('drops bindings another project served when a token load switches the scope', async () => {
    await store.loadCiBindings('p-1');
    expect(store.ciBindings().length).toBeGreaterThan(0);

    // The project is the scope of the whole tab: p-1's bindings must not survive under p-2's
    // heading, even though the page has not re-read the binding section yet.
    await store.load('p-2');

    expect(store.ciBindings()).toEqual([]);
    expect(store.ciBindingsPhase()).toBe('idle');
    expect(store.ciBindingsProjectId()).toBeNull();
    expect(store.hasMoreCiBindings()).toBe(false);
    expect(store.ciBindingsError()).toBeNull();
    // The token route was read for p-2, and no binding route was read behind it.
    expect(data.tokenListCalls).toEqual(['p-2']);
    expect(data.listCalls.map((call) => call.projectId)).toEqual(['p-1']);
  });

  it('keeps bindings a page read FIRST when the token load names the same project', async () => {
    // The page may read the binding section before the token list (two effects, one project). A
    // token load of the SAME project must not bump the binding generation and leave fresh rows
    // stuck at `idle` — the guard compares the binding track's project, not "anything changed".
    await store.load('p-1');
    await store.loadCiBindings('p-2');

    await store.load('p-2');

    expect(store.ciBindings()).toEqual(PAGE.items);
    expect(store.ciBindingsPhase()).toBe('ready');
    expect(store.ciBindingsProjectId()).toBe('p-2');
    // One binding read in total: the token load neither dropped nor re-read the rows.
    expect(data.listCalls).toEqual([{ projectId: 'p-2', cursor: undefined }]);
    expect(data.tokenListCalls).toEqual(['p-1', 'p-2']);
  });

  it('never rewrites the token list when a binding read names another project', async () => {
    // One-directional by design: the token read owns the store's scope and the binding track only
    // follows it, so a binding read can never blank the tab's primary list.
    data.tokenPage = { items: [TOKEN_ROW], next_cursor: null };
    await store.load('p-1');
    expect(store.rows()).toHaveLength(1);

    await store.loadCiBindings('p-2');

    expect(store.rows()).toEqual([TOKEN_ROW]);
    expect(store.projectId()).toBe('p-1');
    expect(store.phase()).toBe('ready');
    expect(store.ciBindings()).toEqual(PAGE.items);
    expect(store.ciBindingsProjectId()).toBe('p-2');
  });

  it('drops the whole track on resetCiBindings(), and abandons a read in flight', async () => {
    await store.loadCiBindings('p-1');
    data.listManual = true;
    const inFlight = store.loadCiBindings('p-1');

    store.resetCiBindings();

    expect(store.ciBindings()).toEqual([]);
    expect(store.ciBindingsPhase()).toBe('idle');
    expect(store.ciBindingsError()).toBeNull();
    expect(store.ciBindingsProjectId()).toBeNull();
    expect(store.hasMoreCiBindings()).toBe(false);

    // The read that was open when the track was dropped settles into nothing.
    data.settleList(0, PAGE);
    await inFlight;
    expect(store.ciBindings()).toEqual([]);
    expect(store.ciBindingsPhase()).toBe('idle');
  });

  it('holds no challenge and no secret-shaped field in any public signal (D5, S3, AC3)', async () => {
    data.pages = [
      { items: PAGE.items, next_cursor: '1' },
      { items: [], next_cursor: null },
    ];
    await store.loadCiBindings('p-1');
    await store.loadMoreCiBindings();
    expect(store.ciBindings()).toEqual(PAGE.items);

    // Every public signal of the binding track, serialised: no challenge, no value of any kind.
    const publicState = {
      ciBindings: store.ciBindings(),
      ciBindingsPhase: store.ciBindingsPhase(),
      ciBindingsError: store.ciBindingsError(),
      ciBindingsProjectId: store.ciBindingsProjectId(),
      hasMoreCiBindings: store.hasMoreCiBindings(),
      rows: store.rows(),
      rotateState: store.rotateState(),
    };
    const serialised = JSON.stringify(publicState);
    expect(serialised).not.toContain('challenge');
    expect(serialised).not.toContain('binding_challenge');
    // The corpus page itself carries none either — that is what makes the assertion above mean
    // something rather than pass on an empty list.
    expect(JSON.stringify(PAGE)).not.toContain('challenge');

    // And no field of the store is even named like a value: metadata names only. A binding row is a
    // `CiBindingView`, which the contract says never holds the challenge or its hash.
    const fieldNames = Object.getOwnPropertyNames(store);
    expect(fieldNames).toContain('ciBindings');
    expect(
      fieldNames.filter((name) =>
        /token|secret|password|plaintext|credential|challenge/i.test(name),
      ),
    ).toEqual([]);
    for (const row of store.ciBindings()) {
      expect(Object.keys(row)).not.toContain('binding_challenge');
      expect(Object.keys(row)).not.toContain('challenge');
    }
  });
});
