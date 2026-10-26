// 09.7.1-T6 (plan 09.7.1 §2 AC7/AC8, §4 audit row, §8 S5-adjacent + hot path, §9 "Audit export
// cap", §10 T6.1/T6.2; COORDINATOR-DECISION S76): the Audit tab's store.
//
// `AuditDataService` is replaced by a hand-written fake (`list(orgId, query, cursor)`), because
// this suite is about what the store does with pages and answers — the request side is
// `core/data/audit-data.service.spec.ts`. The fake pages a generated corpus, so the cap cases are
// real page counts rather than assertions about intentions:
//
// - a 15,000-row corpus is 75 pages of 200, and AC7's export must stop at 50 pages / 10,000 rows;
// - a 3,000-row corpus ends by itself in 15 pages and is not capped;
// - exactly 10,000 rows with no next cursor is not capped either — the banner is about rows left
//   behind, not about the count reaching the cap;
// - 50-row pages are a supported server, not a broken one: 3,000 rows finish in 60 pages, uncapped,
//   and a server that never stops naming a cursor finishes as `capped` at the export's page bound
//   with the rows it read — the short-page case that used to fail with a retryable-looking error;
// - the table's own bound is 200 pages / 10,000 rows (`browseCapped`), not the shared
//   `MAX_CURSOR_PAGES`: reaching it stops the list without a failure and without a request;
// - a repeated cursor is a contract violation, not an export or a list;
// - an export is generation-guarded: a reset, a reload and store destruction each supersede it, and
//   a superseded loop publishes nothing and reads no further page.
//
// The rest pins the list's rules: the newest `load` wins, `loadMore` appends and keeps the rows on
// a refusal, the filters reach the wire query with `''` omitted, and no signal holds a
// secret-shaped field (the `no-secret-field-in-store` rule's runtime twin).

import { TestBed } from '@angular/core/testing';
import type { AuditEntryDto, AuditListResponse } from '@evidgate/sdk';

import { AuditDataService, type AuditListQuery } from '../../../core/data/audit-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { AUDIT_EXPORT_CAPPED_EVENT } from '../../../core/telemetry/telemetry-names';
import { TelemetryService } from '../../../core/telemetry/telemetry.service';
import {
  AUDIT_EXPORT_MAX_PAGES,
  AUDIT_MAX_PAGES,
  AuditStore,
  EMPTY_AUDIT_FILTERS,
  type AuditFilters,
} from './audit.store';

/** One typed refusal, as `ApiGateway` builds it from a server envelope. */
function typedFailure(): ApiRequestError {
  return new ApiRequestError({
    kind: 'forbidden',
    status: 403,
    code: 'forbidden',
    message: 'The caller may not read this org audit log.',
    requestId: 'req-audit-1',
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  });
}

/** The generated corpus: `count` rows, newest first, every fifth one a support-access row. */
function rows(count: number): AuditEntryDto[] {
  return Array.from({ length: count }, (_, index) => ({
    id: `ae-${index + 1}`,
    seq: index + 1,
    actor_type: index % 5 === 0 ? 'platform_operator' : 'user',
    actor_id: index % 5 === 0 ? 'op-1' : `u-${index + 1}`,
    action: index % 3 === 0 ? 'membership.invite' : 'secret.rotate',
    object_type: 'membership',
    object_id: `m-${index + 1}`,
    before_hash: null,
    after_hash: null,
    request_id: `req-${index + 1}`,
    trace_id: `trace-${index + 1}`,
    metadata: index % 5 === 0 ? { support_grant_id: 'sg-1' } : null,
    prev_hash: `h-${index}`,
    entry_hash: `h-${index + 1}`,
    created_at: new Date(Date.UTC(2026, 9, 1, 0, 0, index)).toISOString(),
  }));
}

/** The filters a filter case applies, one per wire parameter. */
const FILTERS: AuditFilters = {
  actorType: 'platform_operator',
  action: 'membership.invite',
  objectType: 'membership',
  from: '2026-10-01T00:00:00Z',
  to: '2026-10-02T00:00:00Z',
};

/**
 * The audit pages the store reads. Cursors are opaque strings the fake mints itself, so a page can
 * hand out a next cursor the test controls exactly (a fresh index, the same one again, or one).
 */
class FakeAuditDataService {
  /** Every row the fake can serve, in server order. */
  all: AuditEntryDto[] = [];
  /** Rejects every call with this failure when set (used with `failOnCall` for a mid-way one). */
  failure: Error | null = null;
  /** Rejects exactly this 1-based call with `failure`. */
  failOnCall: number | null = null;
  /** Serves this many rows per page regardless of the requested `limit`. */
  pageSizeOverride: number | null = null;
  /** Names the same cursor on every page, so a cursor loop has to be refused. */
  repeatCursor = false;
  /** Answers only when the test settles the matching `pending` entry. */
  manual = false;
  readonly pending: Array<{ readonly resolve: (page: AuditListResponse) => void }> = [];
  /** Every call the store made, in order. */
  readonly calls: Array<{
    readonly orgId: string;
    readonly query: AuditListQuery;
    readonly cursor: string | undefined;
  }> = [];

  /** The index a minted cursor resumes at. */
  private readonly startByCursor = new Map<string, number>();

  list(orgId: string, query: AuditListQuery, cursor?: string): Promise<AuditListResponse> {
    this.calls.push({ orgId, query, cursor });
    if (this.manual) {
      return new Promise<AuditListResponse>((resolve) => {
        this.pending.push({ resolve });
      });
    }
    if (
      this.failure !== null &&
      (this.failOnCall === null || this.failOnCall === this.calls.length)
    ) {
      return Promise.reject(this.failure);
    }

    const start = cursor === undefined ? 0 : (this.startByCursor.get(cursor) ?? 0);
    const size = this.pageSizeOverride ?? query.limit ?? this.all.length;
    const items = this.all.slice(start, start + size);
    const end = start + items.length;
    let next: string | null = null;
    if (end < this.all.length) {
      next = this.repeatCursor ? 'same' : String(end);
      this.startByCursor.set(next, end);
    }
    return Promise.resolve({ items, next_cursor: next });
  }
}

describe('AuditStore (09.7.1-T6)', () => {
  let data: FakeAuditDataService;
  let store: InstanceType<typeof AuditStore>;

  beforeEach(() => {
    data = new FakeAuditDataService();
    TestBed.configureTestingModule({
      providers: [AuditStore, { provide: AuditDataService, useValue: data }],
    });
    store = TestBed.inject(AuditStore);
  });

  describe('load', () => {
    it('publishes the first page at the table limit and offers the next one', async () => {
      data.all = rows(120);
      expect(store.phase()).toBe('idle');

      await store.load('o-1');

      expect(store.phase()).toBe('ready');
      expect(store.rows().map((row) => row.seq)).toEqual(
        Array.from({ length: 50 }, (_, index) => index + 1),
      );
      expect(store.nextCursor()).toBe('50');
      expect(store.hasMore()).toBe(true);
      expect(store.loadError()).toBeNull();
      expect(store.filters()).toEqual(EMPTY_AUDIT_FILTERS);
      // One read of the first page, at the table limit, with no cursor.
      expect(data.calls).toEqual([{ orgId: 'o-1', query: { limit: 50 }, cursor: undefined }]);
    });

    it('maps every filter and omits the ones left empty', async () => {
      data.all = rows(3);

      await store.load('o-1', FILTERS);

      expect(data.calls[0]?.query).toEqual({
        limit: 50,
        actor_type: 'platform_operator',
        action: 'membership.invite',
        object_type: 'membership',
        from: '2026-10-01T00:00:00Z',
        to: '2026-10-02T00:00:00Z',
      });
      expect(store.filters()).toEqual(FILTERS);

      await store.load('o-1');

      // No filters passed: the applied ones stay applied, on the wire and in state.
      expect(data.calls[1]?.query).toEqual(data.calls[0]?.query);
      expect(store.filters()).toEqual(FILTERS);
    });

    it('is the tab error state with the server’s failure, and clears the rows', async () => {
      const failure = typedFailure();
      data.failure = failure;

      await store.load('o-1');

      expect(store.phase()).toBe('error');
      expect(store.loadError()).toBe(failure.error);
      expect(store.rows()).toEqual([]);
      expect(store.hasMore()).toBe(false);
    });

    it('reports a rejection that is not the server’s answer as a fixed failure', async () => {
      data.failure = new Error('adapter cancelled the request for org o-1');

      await store.load('o-1');

      expect(store.phase()).toBe('error');
      // No text of the thrown value reaches the screen — least of all its org id.
      expect(store.loadError()).toMatchObject({ kind: 'unexpected', code: 'request_unresolved' });
      expect(store.loadError()?.message).not.toContain('o-1');
    });

    it('clears the previous rows while the new first page is in flight', async () => {
      data.all = rows(4);
      await store.load('o-1');
      expect(store.rows()).toHaveLength(4);

      data.manual = true;
      const reload = store.load('o-1', { ...EMPTY_AUDIT_FILTERS, action: 'secret.rotate' });

      expect(store.phase()).toBe('loading');
      expect(store.rows()).toEqual([]);
      expect(store.nextCursor()).toBeNull();

      data.pending[0]?.resolve({ items: rows(2), next_cursor: null });
      await reload;
      expect(store.phase()).toBe('ready');
      expect(store.rows()).toHaveLength(2);
    });

    it('lets the newest load win when two answers arrive out of order', async () => {
      data.manual = true;

      const first = store.load('o-1');
      const second = store.load('o-2');
      expect(data.pending).toHaveLength(2);

      data.pending[1]?.resolve({ items: rows(2), next_cursor: null });
      await second;
      data.pending[0]?.resolve({ items: rows(3), next_cursor: 'stale' });
      await first;

      // The stale answer published nothing: neither its rows nor its cursor.
      expect(store.rows()).toHaveLength(2);
      expect(store.nextCursor()).toBeNull();
      expect(store.hasMore()).toBe(false);
    });
  });

  describe('loadMore', () => {
    it('follows the cursor once, appends the page and keeps one query shape', async () => {
      data.all = rows(120);
      await store.load('o-1', FILTERS);

      await store.loadMore('o-1');

      expect(store.rows()).toHaveLength(100);
      expect(store.rows().at(-1)?.seq).toBe(100);
      expect(store.nextCursor()).toBe('100');
      expect(store.paging()).toBe(false);
      expect(data.calls[1]).toEqual({
        orgId: 'o-1',
        query: { ...data.calls[0]?.query },
        cursor: '50',
      });
    });

    it('does nothing without a cursor', async () => {
      data.all = rows(10);
      await store.load('o-1');

      await store.loadMore('o-1');

      expect(data.calls).toHaveLength(1);
      expect(store.rows()).toHaveLength(10);
    });

    it('ignores a second page while the first one is still in flight', async () => {
      data.manual = true;
      const loading = store.load('o-1');
      data.pending[0]?.resolve({ items: rows(120).slice(0, 50), next_cursor: '50' });
      await loading;

      const paging = store.loadMore('o-1');
      expect(store.paging()).toBe(true);
      await store.loadMore('o-1');
      expect(data.calls).toHaveLength(2);

      data.pending[1]?.resolve({ items: rows(120).slice(50, 100), next_cursor: '100' });
      await paging;
      expect(store.rows()).toHaveLength(100);
      expect(store.paging()).toBe(false);
      expect(store.nextCursor()).toBe('100');
    });

    it('keeps the rows and reports the refusal when the next page fails', async () => {
      data.all = rows(120);
      await store.load('o-1');
      const failure = typedFailure();
      data.failure = failure;
      data.failOnCall = 2;

      await store.loadMore('o-1');

      expect(store.rows()).toHaveLength(50);
      expect(store.phase()).toBe('ready');
      expect(store.paging()).toBe(false);
      expect(store.pagingError()).toBe(failure.error);
      expect(store.nextCursor()).toBe('50');
    });

    it('refuses a cursor it already followed', async () => {
      data.all = rows(120);
      data.repeatCursor = true;
      await store.load('o-1');
      await store.loadMore('o-1');

      await expect(store.loadMore('o-1')).rejects.toThrow(/repeated cursor "same"/);
    });

    it('stops at its own page bound as a bounded list, not a failure', async () => {
      data.all = rows(AUDIT_MAX_PAGES * 50 + 500);
      await store.load('o-1');
      for (let page = 1; page < AUDIT_MAX_PAGES; page += 1) {
        await store.loadMore('o-1');
      }

      expect(store.rows()).toHaveLength(AUDIT_MAX_PAGES * 50);
      // The log goes on, but the list is at its bound: the cursor stays, the control goes.
      expect(store.nextCursor()).not.toBeNull();
      expect(store.browseCapped()).toBe(true);
      expect(store.hasMore()).toBe(false);
      expect(store.pagingError()).toBeNull();

      // A further click neither throws nor reads: there is nothing left the tab may offer.
      const calls = data.calls.length;
      await expect(store.loadMore('o-1')).resolves.toBeUndefined();
      expect(data.calls).toHaveLength(calls);
      expect(store.browseCapped()).toBe(true);

      // A reload is the way back: the bound belongs to the list it was reached on.
      await store.load('o-1');
      expect(store.browseCapped()).toBe(false);
      expect(store.hasMore()).toBe(true);
    });

    it('marks a short-page list capped at the bound with the rows it holds', async () => {
      // 20-row pages: 200 pages is 4,000 rows, not the 10,000 the table limit would give, and the
      // bound is the page count — the state must report what it holds, not a fixed row total.
      data.all = rows(5_000);
      data.pageSizeOverride = 20;
      await store.load('o-1');
      for (let page = 1; page < AUDIT_MAX_PAGES; page += 1) {
        await store.loadMore('o-1');
      }

      expect(store.rows()).toHaveLength(AUDIT_MAX_PAGES * 20);
      expect(store.rows().at(-1)?.seq).toBe(AUDIT_MAX_PAGES * 20);
      expect(store.nextCursor()).not.toBeNull();
      expect(store.browseCapped()).toBe(true);
      expect(store.hasMore()).toBe(false);
    });

    it('ignores a page for an org this store is not showing', async () => {
      data.all = rows(120);
      await store.load('o-1');

      await store.loadMore('o-2');

      expect(data.calls).toHaveLength(1);
    });
  });

  describe('exportCsv (AC7)', () => {
    it('caps a 15,000-row export at 10,000 rows in 50 pages of 200', async () => {
      data.all = rows(15_000);
      await store.load('o-1');
      data.calls.length = 0;

      const csv = await store.exportCsv('o-1');

      // Header + 10,000 rows + the trailing newline.
      const exported = csv?.split('\r\n') ?? [];
      expect(exported).toHaveLength(10_002);
      expect(exported[0]).toBe(
        'seq,created_at,actor_type,actor_id,action,object_type,object_id,request_id,support_grant_id,metadata',
      );
      expect(exported[1]?.startsWith('1,')).toBe(true);
      // The last exported row is the 10,000th; the 10,001st is exactly what the cap withheld.
      expect(exported[10_000]?.startsWith('10000,')).toBe(true);
      expect(csv).not.toContain('10001,');
      // 75 pages exist; the cap stops the loop after 50.
      expect(data.calls).toHaveLength(50);
      expect(data.calls.every((call) => call.query.limit === 200)).toBe(true);
      expect(data.calls[0]?.cursor).toBeUndefined();
      expect(store.exportState()).toEqual({ phase: 'done', rows: 10_000, capped: true });
      // The export's rows are not state: the loaded page is still exactly the page it was.
      expect(store.rows()).toHaveLength(50);
    });

    it('exports a 3,000-row list whole, without the cap', async () => {
      data.all = rows(3_000);

      const csv = await store.exportCsv('o-1');

      expect(csv?.split('\r\n')).toHaveLength(3_002);
      expect(data.calls).toHaveLength(15);
      expect(store.exportState()).toEqual({ phase: 'done', rows: 3_000, capped: false });
    });

    it('does not cap exactly 10,000 rows that end the list', async () => {
      data.all = rows(10_000);

      await store.exportCsv('o-1');

      expect(data.calls).toHaveLength(50);
      expect(store.exportState()).toEqual({ phase: 'done', rows: 10_000, capped: false });
    });

    it('exports the filters on screen, not the ones a later load applies', async () => {
      data.all = rows(120);
      await store.load('o-1', FILTERS);
      data.calls.length = 0;

      await store.exportCsv('o-1');

      expect(data.calls[0]?.query).toEqual({
        limit: 200,
        actor_type: 'platform_operator',
        action: 'membership.invite',
        object_type: 'membership',
        from: '2026-10-01T00:00:00Z',
        to: '2026-10-02T00:00:00Z',
      });
    });

    it('fails to null when a page mid-way refuses', async () => {
      data.all = rows(15_000);
      const failure = typedFailure();
      data.failure = failure;
      data.failOnCall = 3;

      const csv = await store.exportCsv('o-1');

      expect(csv).toBeNull();
      expect(data.calls).toHaveLength(3);
      expect(store.exportState()).toEqual({ phase: 'failed', error: failure.error });
    });

    it('refuses a repeated cursor', async () => {
      data.all = rows(1_000);
      data.repeatCursor = true;

      const csv = await store.exportCsv('o-1');

      expect(csv).toBeNull();
      expect(data.calls).toHaveLength(2);
      expect(store.exportState().phase).toBe('failed');
    });

    it('exports a 50-row-page list whole, uncapped, past the row cap', async () => {
      data.all = rows(3_000);
      data.pageSizeOverride = 50;

      const csv = await store.exportCsv('o-1');

      // 60 pages of 50: more than the 50 pages 10,000 rows cost at the wire maximum, and complete.
      expect(csv?.split('\r\n')).toHaveLength(3_002);
      expect(data.calls).toHaveLength(60);
      expect(data.calls.every((call) => call.query.limit === 200)).toBe(true);
      expect(store.exportState()).toEqual({ phase: 'done', rows: 3_000, capped: false });
    });

    it('caps a server that never ends at the page bound, without failing', async () => {
      data.all = rows(1_000);
      data.pageSizeOverride = 1;
      const reported = vi.spyOn(TestBed.inject(TelemetryService), 'reportEvent');

      const csv = await store.exportCsv('o-1');

      // Header + the rows read + the trailing newline: one row per page, one page past the bound.
      expect(csv?.split('\r\n')).toHaveLength(AUDIT_EXPORT_MAX_PAGES + 2);
      expect(data.calls).toHaveLength(AUDIT_EXPORT_MAX_PAGES);
      // Capped, and the count the banner must print is the real one — below the 10,000-row cap.
      expect(store.exportState()).toEqual({
        phase: 'done',
        rows: AUDIT_EXPORT_MAX_PAGES,
        capped: true,
      });
      // A cap hit is a cap hit: the page bound logs the same one event, with the name alone.
      expect(reported.mock.calls).toEqual([[AUDIT_EXPORT_CAPPED_EVENT]]);
    });

    it('ignores a second export while one is running, and resets to idle', async () => {
      data.manual = true;
      const running = store.exportCsv('o-1');
      expect(store.exportState()).toEqual({ phase: 'running', rows: 0 });

      expect(await store.exportCsv('o-1')).toBeNull();
      expect(data.calls).toHaveLength(1);

      data.pending[0]?.resolve({ items: rows(2), next_cursor: null });
      const csv = await running;
      expect(store.exportState()).toEqual({ phase: 'done', rows: 2, capped: false });
      expect(csv?.split('\r\n')).toHaveLength(4);

      store.resetExport();
      expect(store.exportState()).toEqual({ phase: 'idle' });
    });

    it('records one audit.export_capped event when the export is capped, and nothing else', async () => {
      data.all = rows(15_000);
      const reported = vi.spyOn(TestBed.inject(TelemetryService), 'reportEvent');

      await store.exportCsv('o-1');

      expect(store.exportState()).toEqual({ phase: 'done', rows: 10_000, capped: true });
      // One event, one argument: the name is the whole payload — no row count, no org id, no
      // filter value (plan §8, ids only; the event itself carries no attribute).
      expect(reported.mock.calls).toEqual([[AUDIT_EXPORT_CAPPED_EVENT]]);
      expect(JSON.stringify(reported.mock.calls)).not.toContain('10000');
      expect(JSON.stringify(reported.mock.calls)).not.toContain('o-1');
    });

    it('records no event when the export is not capped', async () => {
      data.all = rows(3_000);
      const reported = vi.spyOn(TestBed.inject(TelemetryService), 'reportEvent');

      await store.exportCsv('o-1');

      expect(store.exportState()).toEqual({ phase: 'done', rows: 3_000, capped: false });
      expect(reported).not.toHaveBeenCalled();
    });

    it('records no event for an export the page already dropped', async () => {
      data.all = rows(15_000);
      data.manual = true;
      const reported = vi.spyOn(TestBed.inject(TelemetryService), 'reportEvent');

      const running = store.exportCsv('o-1');
      store.resetExport();
      data.pending[0]?.resolve({ items: rows(4), next_cursor: 'more' });

      expect(await running).toBeNull();
      expect(reported).not.toHaveBeenCalled();
    });
  });

  describe('exportCsv supersession (org switch, reset, lifetime)', () => {
    it('publishes nothing and reads no further page after a reset', async () => {
      data.manual = true;
      const running = store.exportCsv('o-1');
      expect(store.exportState()).toEqual({ phase: 'running', rows: 0 });

      store.resetExport();
      expect(store.exportState()).toEqual({ phase: 'idle' });

      // The in-flight page arrives with a cursor: the reset loop must neither publish nor follow it.
      data.pending[0]?.resolve({ items: rows(2), next_cursor: 'next' });
      expect(await running).toBeNull();
      expect(store.exportState()).toEqual({ phase: 'idle' });
      expect(data.calls).toHaveLength(1);
    });

    it('leaves the new org idle when the org switched mid-export', async () => {
      data.manual = true;
      const stale = store.exportCsv('o-1');
      expect(data.pending).toHaveLength(1);

      // Exactly what audit.page.ts does on an org switch: drop the export, then load the new org.
      store.resetExport();
      const reload = store.load('o-2');
      expect(data.pending).toHaveLength(2);

      data.pending[0]?.resolve({ items: rows(4), next_cursor: 'more' });
      expect(await stale).toBeNull();
      expect(store.exportState()).toEqual({ phase: 'idle' });

      data.pending[1]?.resolve({ items: rows(1), next_cursor: null });
      await reload;
      // The new org's list is in state; the old org's export left no banner and read no more.
      expect(store.rows()).toHaveLength(1);
      expect(store.exportState()).toEqual({ phase: 'idle' });
      expect(data.calls).toHaveLength(2);
      expect(data.calls[1]?.orgId).toBe('o-2');
    });

    it('lets a second export start after the reset and publishes only that one', async () => {
      data.manual = true;
      const first = store.exportCsv('o-1');
      store.resetExport();
      const second = store.exportCsv('o-1');
      expect(store.exportState()).toEqual({ phase: 'running', rows: 0 });
      expect(data.pending).toHaveLength(2);

      data.pending[0]?.resolve({ items: rows(5), next_cursor: 'stale' });
      expect(await first).toBeNull();

      data.pending[1]?.resolve({ items: rows(2), next_cursor: null });
      const csv = await second;
      expect(csv?.split('\r\n')).toHaveLength(4);
      expect(store.exportState()).toEqual({ phase: 'done', rows: 2, capped: false });
      expect(data.calls).toHaveLength(2);
    });

    it('stops a running export when its owner is destroyed', async () => {
      data.manual = true;
      const running = store.exportCsv('o-1');
      expect(store.exportState()).toEqual({ phase: 'running', rows: 0 });

      // The tab unmounts and the store's injection context goes with it.
      TestBed.resetTestingModule();

      data.pending[0]?.resolve({ items: rows(3), next_cursor: 'next' });
      expect(await running).toBeNull();
      // No banner for a store nobody renders, and no page after the in-flight one.
      expect(store.exportState()).toEqual({ phase: 'running', rows: 0 });
      expect(data.calls).toHaveLength(1);
    });
  });

  it('holds no secret-shaped field in any of its signals (D5, S3)', async () => {
    data.all = rows(120);
    await store.load('o-1');
    // A page that refuses: the rows stay on screen and the refusal is in state with them.
    data.failure = typedFailure();
    data.failOnCall = data.calls.length + 1;
    await store.loadMore('o-1');
    await store.exportCsv('o-1');

    const keys = new Set<string>();
    const collect = (value: unknown): void => {
      if (Array.isArray(value)) {
        for (const item of value) collect(item);
        return;
      }
      if (value !== null && typeof value === 'object') {
        for (const [key, child] of Object.entries(value)) {
          keys.add(key);
          collect(child);
        }
      }
    };
    collect([
      store.rows(),
      store.filters(),
      store.nextCursor(),
      store.phase(),
      store.loadError(),
      store.paging(),
      store.pagingError(),
      store.exportState(),
    ]);

    // The state was serialised for real: a row, its metadata and a refusal are in it.
    expect(keys.has('entry_hash')).toBe(true);
    expect(keys.has('support_grant_id')).toBe(true);
    expect(keys.has('request_id')).toBe(true);
    expect(
      [...keys].filter((key) => /token|secret|password|credential|plaintext/i.test(key)),
    ).toEqual([]);
  });
});
