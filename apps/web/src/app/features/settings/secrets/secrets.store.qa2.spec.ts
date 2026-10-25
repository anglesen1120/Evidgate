// QA2 independent gate (09.7.1-T3 AC3 "cursor pagination" + S82 P1/P5): the interleavings the
// acceptance criteria name but the suite's own specs test one at a time — a cursor page that
// answers after the project moved, a rotation refresh that must win over a cursor page still in
// flight, and a second row pressed while one rotation is out. Real `SecretsStore`, fake data
// service; the fixture corpus supplies the shapes.
import { TestBed } from '@angular/core/testing';
import type {
  IngestTokenCreatedResponse,
  IngestTokenListResponse,
  IngestTokenView,
} from '@evidgate/sdk';

import { SecretsDataService } from '../../../core/data/secrets-data.service';
import { SecretsStore } from './secrets.store';

import rotateFixture from '../../../../../../../fixtures/web-api/ingest-token-rotate/input.json';
import tokenListFixture from '../../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-token/input.json';

const PAGE = tokenListFixture.body as unknown as IngestTokenListResponse;
const ROTATED = rotateFixture.body as unknown as IngestTokenCreatedResponse;

function firstRow(): IngestTokenView {
  const row = PAGE.items[0];
  if (row === undefined) throw new Error('the ingest-token fixture has no row 0');
  return row;
}

const BASE_ROW = firstRow();
const SUCCESSOR_ID = ROTATED.token.id;

/** A row of one project, so a page that leaked across projects is visible by its id alone. */
function row(id: string): IngestTokenView {
  return { ...BASE_ROW, id, prefix: id };
}

/**
 * Serves one first page per project immediately (`<project>-first`, exactly one next cursor) and
 * defers every cursor page, so a test decides when the second page answers — after a scope switch
 * or after a rotation refresh, the two races this suite is about.
 */
class InterleavedDataService {
  readonly listCalls: Array<{ projectId: string; cursor: string | undefined }> = [];
  private readonly deferredPages: Array<{ resolve: (page: IngestTokenListResponse) => void }> = [];
  readonly rotateCalls: string[] = [];
  private readonly pendingRotations: Array<{
    resolve: (created: IngestTokenCreatedResponse) => void;
  }> = [];
  deferCursorPages = true;
  rotateManual = false;

  listIngestTokens(projectId: string, cursor?: string): Promise<IngestTokenListResponse> {
    this.listCalls.push({ projectId, cursor });
    if (cursor !== undefined && this.deferCursorPages) {
      return new Promise<IngestTokenListResponse>((resolve) => {
        this.deferredPages.push({ resolve });
      });
    }
    return Promise.resolve({
      items: [row(cursor === undefined ? `${projectId}-first` : `${projectId}-late`)],
      next_cursor: cursor === undefined ? `${projectId}-c2` : null,
    });
  }

  rotateIngestToken(tokenId: string): Promise<IngestTokenCreatedResponse> {
    this.rotateCalls.push(tokenId);
    if (this.rotateManual) {
      return new Promise<IngestTokenCreatedResponse>((resolve) => {
        this.pendingRotations.push({ resolve });
      });
    }
    return Promise.resolve(ROTATED);
  }

  /** Answers the n-th deferred cursor page. */
  settleCursorPage(index: number, page: IngestTokenListResponse): void {
    const pending = this.deferredPages[index];
    if (pending === undefined) throw new Error(`the fake holds no deferred page ${index}`);
    pending.resolve(page);
  }

  /** Answers the n-th deferred rotation with the server's successor. */
  settleRotation(index: number): void {
    const pending = this.pendingRotations[index];
    if (pending === undefined) throw new Error(`the fake holds no pending rotation ${index}`);
    pending.resolve(ROTATED);
  }
}

describe('SecretsStore interleavings (09.7.1-T3 QA2)', () => {
  let data: InterleavedDataService;
  let store: SecretsStore;

  beforeEach(() => {
    data = new InterleavedDataService();
    TestBed.configureTestingModule({
      providers: [SecretsStore, { provide: SecretsDataService, useValue: data }],
    });
    store = TestBed.inject(SecretsStore);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('drops a cursor page that answers after the project switched instead of appending it', async () => {
    await store.load('p1');
    expect(store.rows().map((value) => value.id)).toEqual(['p1-first']);
    expect(store.hasMore()).toBe(true);

    const paging = store.loadMore();
    // The reader moves on before the second page answers.
    await store.load('p2');
    data.settleCursorPage(0, { items: [row('p1-late')], next_cursor: null });
    await paging;

    // The new project's list is exactly its own first page: no row, and no cursor, of the old one.
    expect(store.projectId()).toBe('p2');
    expect(store.rows().map((value) => value.id)).toEqual(['p2-first']);
    expect(store.hasMore()).toBe(true);
    expect(data.listCalls).toEqual([
      { projectId: 'p1', cursor: undefined },
      { projectId: 'p1', cursor: 'p1-c2' },
      { projectId: 'p2', cursor: undefined },
    ]);
  });

  it('drops a cursor page still in flight when a rotation refreshes the list', async () => {
    await store.load('p1');
    const paging = store.loadMore();

    const rotated = await store.rotate(BASE_ROW.id);
    expect(rotated?.successorId).toBe(SUCCESSOR_ID);

    // The stale page belongs to the list the refresh replaced; it must not be appended to it.
    data.settleCursorPage(0, { items: [row('p1-late')], next_cursor: null });
    await paging;

    expect(store.rows().map((value) => value.id)).toEqual(['p1-first']);
    expect(store.rows().some((value) => value.id === 'p1-late')).toBe(false);
  });

  it('ignores a rotation of another row while one rotation is out', async () => {
    await store.load('p1');
    data.rotateManual = true;

    const first = store.rotate('token-a');
    await Promise.resolve();
    expect(store.rotateState()).toEqual({ phase: 'submitting', tokenId: 'token-a', error: null });

    await expect(store.rotate('token-b')).resolves.toBeNull();
    expect(data.rotateCalls).toEqual(['token-a']);

    data.settleRotation(0);
    await expect(first).resolves.not.toBeNull();
    expect(store.rotateState().phase).toBe('done');
  });

  it('rotates the same row again only after the first request settled', async () => {
    await store.load('p1');
    data.rotateManual = true;

    const first = store.rotate(BASE_ROW.id);
    await Promise.resolve();
    await expect(store.rotate(BASE_ROW.id)).resolves.toBeNull();
    data.settleRotation(0);
    await expect(first).resolves.not.toBeNull();

    // Once the request settled the lock is gone, so the successor can be rotated in turn.
    const second = store.rotate(BASE_ROW.id);
    await Promise.resolve();
    data.settleRotation(1);
    await expect(second).resolves.not.toBeNull();
    expect(data.rotateCalls).toEqual([BASE_ROW.id, BASE_ROW.id]);
  });
});
