import { TestBed } from '@angular/core/testing';
import type { SuiteRevisionDto } from '@evidgate/sdk';
import ready from '../../../../../../../fixtures/web-api/suite-revision/input.json';
import published from '../../../../../../../fixtures/web-api/suite-revision-published/input.json';
import { SuitesDataService } from '../../../core/data/suites-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../../core/http/api-error';
import { SuiteCompositionStore } from './suite-composition.store';

const revision = ready.body as SuiteRevisionDto;
const nextRevision = published.body as SuiteRevisionDto;

function refusal(kind: ApiErrorKind): ApiRequestError {
  return new ApiRequestError({
    kind,
    status: 0,
    code: kind,
    message: '',
    requestId: null,
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  });
}

/**
 * The deferred resolver by hand: `Promise.withResolvers` is ES2024 and this project compiles
 * against the ES2022 lib, so the executor is how the handles escape (`core/http/http-client-fetch.ts`
 * spells the reject handle the same way). The executor runs synchronously, so the stored handles
 * are real by the time a test settles a load.
 */
function deferred<T>() {
  let resolve: (value: T) => void = () => undefined;
  let reject: (reason?: unknown) => void = () => undefined;
  const promise = new Promise<T>((settle, fail) => {
    resolve = settle;
    reject = fail;
  });
  return { promise, resolve, reject };
}

describe('SuiteCompositionStore', () => {
  const getRevision = vi.fn<(id: string) => Promise<SuiteRevisionDto>>();
  beforeEach(() => {
    getRevision.mockReset().mockResolvedValue(revision);
    TestBed.configureTestingModule({
      providers: [SuiteCompositionStore, { provide: SuitesDataService, useValue: { getRevision } }],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  it('starts idle and derives the matrix only after a revision is ready', async () => {
    const store = TestBed.inject(SuiteCompositionStore);
    expect(store.status()).toBe('idle');
    expect(store.revision()).toBeNull();
    expect(store.view()).toBeNull();
    await store.load(revision.id);
    expect(store.status()).toBe('ready');
    expect(store.revision()).toEqual(revision);
    expect(store.view()?.rows).toHaveLength(3);
    expect(store.loadError()).toBeNull();
  });

  it('clears the old composition while another revision is loading', async () => {
    const store = TestBed.inject(SuiteCompositionStore);
    await store.load(revision.id);
    const pending = deferred<SuiteRevisionDto>();
    getRevision.mockReturnValueOnce(pending.promise);
    const loading = store.load(nextRevision.id);
    expect(store.status()).toBe('loading');
    expect(store.revision()).toBeNull();
    expect(store.view()).toBeNull();
    pending.resolve(nextRevision);
    await loading;
    expect(store.revision()).toEqual(nextRevision);
  });

  it.each([
    ['not_found', 'not-found'],
    ['forbidden', 'forbidden'],
    ['unavailable', 'unavailable'],
    ['network', 'error'],
    ['timeout', 'error'],
    ['unexpected', 'error'],
  ] as const)('maps the typed %s failure to %s', async (kind, status) => {
    const store = TestBed.inject(SuiteCompositionStore);
    await store.load(revision.id);
    const failure = refusal(kind);
    getRevision.mockRejectedValueOnce(failure);
    await store.load('unreadable');
    expect(store.status()).toBe(status);
    expect(store.loadError()).toEqual(failure.error);
    expect(store.revision()).toBeNull();
    expect(store.view()).toBeNull();
  });

  it('maps an unknown rejection to the run-detail unexpected failure shape', async () => {
    getRevision.mockRejectedValueOnce(new Error('internal error'));
    const store = TestBed.inject(SuiteCompositionStore);
    await store.load(revision.id);
    expect(store.status()).toBe('error');
    expect(store.loadError()).toEqual({
      kind: 'unexpected',
      status: 0,
      code: '',
      message: '',
      requestId: null,
      retryable: false,
      details: [],
      retryAfterSeconds: null,
    });
  });

  it('ignores an older success that settles after the new revision', async () => {
    const old = deferred<SuiteRevisionDto>();
    getRevision.mockReturnValueOnce(old.promise).mockResolvedValueOnce(nextRevision);
    const store = TestBed.inject(SuiteCompositionStore);
    const first = store.load(revision.id);
    await store.load(nextRevision.id);
    old.resolve(revision);
    await first;
    expect(store.revision()).toEqual(nextRevision);
    expect(store.status()).toBe('ready');
    expect(store.loadError()).toBeNull();
  });

  it('ignores an older refusal that settles after the new revision', async () => {
    const old = deferred<SuiteRevisionDto>();
    getRevision.mockReturnValueOnce(old.promise).mockResolvedValueOnce(nextRevision);
    const store = TestBed.inject(SuiteCompositionStore);
    const first = store.load(revision.id);
    await store.load(nextRevision.id);
    old.reject(refusal('forbidden'));
    await first;
    expect(store.revision()).toEqual(nextRevision);
    expect(store.status()).toBe('ready');
    expect(store.loadError()).toBeNull();
  });

  it('does not resurrect an older success over the latest refusal', async () => {
    const old = deferred<SuiteRevisionDto>();
    getRevision.mockReturnValueOnce(old.promise).mockRejectedValueOnce(refusal('not_found'));
    const store = TestBed.inject(SuiteCompositionStore);
    const first = store.load(revision.id);
    await store.load(nextRevision.id);
    old.resolve(revision);
    await first;
    expect(store.status()).toBe('not-found');
    expect(store.revision()).toBeNull();
    expect(store.view()).toBeNull();
  });
});
