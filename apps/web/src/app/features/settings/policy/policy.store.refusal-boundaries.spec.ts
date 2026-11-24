import { TestBed } from '@angular/core/testing';
import { PolicyDataService } from '../../../core/data/policy-data.service';
import { SessionStore } from '../../../core/session/session.store';
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

describe('PolicyStore history authorization boundaries', () => {
  afterEach(() => TestBed.resetTestingModule());

  it.each(
    (['next', 'previous', 'retry', 'size'] as const).flatMap((operation) =>
      (
        [
          { kind: 'forbidden', status: 403 },
          { kind: 'unauthenticated', status: 401 },
          { kind: 'not_found', status: 404 },
        ] as const
      ).map((refusal) => ({ ...refusal, operation })),
    ),
  )('clears retained state after $operation returns $kind', async ({ kind, status, operation }) => {
    const data = new FakePolicyDataService();
    data.revisions.set(ANCHOR_ID, revision(ANCHOR_ID, PROJECT_ID, 1, 'active'));
    data.pages = [
      page([summary(ANCHOR_ID, 1, 'active')], '1', HEAD_ID),
      page([summary(SECOND_ID, 2, 'draft')], null, HEAD_ID),
    ];
    TestBed.configureTestingModule({
      providers: [
        PolicyStore,
        PolicyProjectReads,
        { provide: PolicyDataService, useValue: data },
        { provide: SessionStore, useValue: new FakeSessionStore() },
      ],
    });
    const store = TestBed.inject(PolicyStore);
    await store.open(ANCHOR_ID);
    expect(store.phase()).toBe('ready');
    expect(store.revisions().map((row) => row.id)).toEqual([ANCHOR_ID]);
    expect(store.openedRevision()?.id).toBe(ANCHOR_ID);
    expect((await store.loadBody(ANCHOR_ID)).id).toBe(ANCHOR_ID);
    if (operation === 'previous' || operation === 'size') await store.loadMore();
    if (operation === 'retry') {
      data.listFailure = typedFailure(
        { error: { code: 'unavailable', message: 'Busy', request_id: HEAD_ID, retryable: true } },
        'unavailable',
        503,
      );
      await store.loadMore();
      expect(store.phase()).toBe('ready');
    }
    data.getManual = true;
    const olderBody = store.loadBody(SECOND_ID);

    data.listFailure = typedFailure(
      {
        error: {
          code: kind,
          message: 'This history is no longer readable.',
          request_id: HEAD_ID,
          retryable: false,
        },
      },
      kind,
      status,
    );
    if (operation === 'previous') await store.previousPage();
    else if (operation === 'retry') await store.retryPage();
    else if (operation === 'size') await store.setPageSize(50);
    else await store.loadMore();

    expect(store.loadError()?.kind).toBe(kind);
    expect(store.revisions()).toEqual([]);
    expect(store.openedRevision()).toBeNull();
    expect(store.headRevisionId()).toBeNull();
    expect(store.activation()).toBeNull();
    expect(store.projectId()).toBeNull();
    expect(store.reads.head().value).toBeNull();
    expect(store.reads.activation().value).toBeNull();
    expect(store.hasMore()).toBe(false);
    expect(store.page()).toBe(1);
    expect(store.paging()).toBe(false);
    expect(store.phase()).toBe('not_found');
    // An older body request may answer its caller, but cannot repopulate the cache.
    data.settleGet(0, revision(SECOND_ID, PROJECT_ID, 2, 'draft'));
    await olderBody;
    data.getManual = false;
    data.getFailure = data.listFailure;
    await expect(store.loadBody(ANCHOR_ID)).rejects.toBe(data.getFailure);
    await expect(store.loadBody(SECOND_ID)).rejects.toBe(data.getFailure);

    // Neither a pending destination nor a committed cursor survives the refusal.
    const count = data.listCalls.length;
    await store.loadMore();
    await store.previousPage();
    expect(data.listCalls).toHaveLength(count);
    data.getFailure = null;
    data.listFailure = null;
    await store.retryPage();
    expect(store.phase()).toBe('ready');
    expect(store.page()).toBe(1);
    expect(data.listCalls.at(-1)?.cursor).toBeUndefined();
  });
});
