import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type {
  EvidenceRevisionPage,
  ReleaseDetail,
  RunAttemptPage,
  RunStateDto,
} from '@evidgate/sdk';

import { ReleasesDataService } from '../../../core/data/releases-data.service';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { RunDataService } from '../data/run-data.service';
import { deferred, pendingBody, refused, run } from './run-detail-store.fixtures';
import { RunDetailStore } from './run-detail.store';

function release(overrides: Partial<ReleaseDetail> = {}): ReleaseDetail {
  return {
    release_id: 'rel-1',
    project_id: 'p-1',
    name: 'Web 2026.11',
    state: 'active',
    created_by: 'user:018f0000-0000-7000-8000-000000000001',
    created_at: '2026-11-01T00:00:00Z',
    version: 2,
    components: [
      {
        component_key: 'web',
        digest: `sha256:${'a'.repeat(64)}`,
        environment_revision_id: 'env-web-3',
      },
    ],
    bound_runs: [{ run_id: run.run_id, component_key: 'web', bound_at: '2026-11-01T00:00:01Z' }],
    ...overrides,
  };
}

describe('RunDetailStore release region', () => {
  const getRun = vi.fn<(id: string) => Promise<RunStateDto>>();
  const listAttempts =
    vi.fn<(id: string, query?: { cursor?: string; limit?: number }) => Promise<RunAttemptPage>>();
  const listEvidenceRevisions =
    vi.fn<
      (id: string, query?: { cursor?: string; limit?: number }) => Promise<EvidenceRevisionPage>
    >();
  const getDecision = vi.fn<(id: string) => Promise<unknown>>();
  const getRelease = vi.fn<(id: string) => Promise<ReleaseDetail>>();

  beforeEach(() => {
    getRun.mockReset().mockResolvedValue(run);
    listAttempts.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    listEvidenceRevisions.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    getDecision.mockReset().mockResolvedValue(pendingBody());
    getRelease.mockReset().mockResolvedValue(release());
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        RunDetailStore,
        {
          provide: RunsDataService,
          useValue: { get: getRun, listAttempts, listEvidenceRevisions },
        },
        { provide: RunDataService, useValue: { getDecision } },
        { provide: ReleasesDataService, useValue: { get: getRelease } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  function store(): InstanceType<typeof RunDetailStore> {
    return TestBed.inject(RunDetailStore);
  }

  it('loadRelease exposes the served release with that release id', async () => {
    const subject = store();
    await subject.loadRelease('rel-1');
    expect(getRelease).toHaveBeenCalledExactlyOnceWith('rel-1');
    expect(subject.releaseStatus()).toBe('ready');
    expect(subject.release()).toEqual(release());
    expect(subject.releaseError()).toBeNull();
  });

  it('drops the previous release while the next read is in flight', async () => {
    const second = deferred<ReleaseDetail>();
    getRelease.mockResolvedValueOnce(release()).mockReturnValueOnce(second.promise);
    const subject = store();
    await subject.loadRelease('rel-1');
    const reading = subject.loadRelease('rel-2');
    expect(subject.release()).toBeNull();
    expect(subject.releaseStatus()).toBe('loading');
    second.resolve(release({ release_id: 'rel-2' }));
    await reading;
    expect(subject.release()?.release_id).toBe('rel-2');
  });

  it('a slower response for the previous release never lands', async () => {
    const first = deferred<ReleaseDetail>();
    const second = deferred<ReleaseDetail>();
    getRelease.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const subject = store();
    const firstRead = subject.loadRelease('rel-1');
    const secondRead = subject.loadRelease('rel-2');
    second.resolve(release({ release_id: 'rel-2' }));
    await secondRead;
    first.resolve(release({ release_id: 'rel-1' }));
    await firstRead;
    expect(subject.release()?.release_id).toBe('rel-2');
    expect(subject.releaseStatus()).toBe('ready');
  });

  it('a run change forgets the release region and its in-flight read', async () => {
    const pending = deferred<ReleaseDetail>();
    getRelease.mockReturnValueOnce(pending.promise);
    const subject = store();
    const reading = subject.loadRelease('rel-1');
    const loading = subject.load(run.run_id);
    expect(subject.release()).toBeNull();
    expect(subject.releaseStatus()).toBe('idle');
    pending.resolve(release());
    await Promise.all([reading, loading]);
    expect(subject.release()).toBeNull();
    expect(subject.releaseStatus()).toBe('idle');
  });

  it('maps a refused read to its region state without touching the run read', async () => {
    getRelease.mockRejectedValueOnce(refused('not_found'));
    const subject = store();
    await subject.loadRelease('missing');
    expect(subject.releaseStatus()).toBe('not-found');
    expect(subject.release()).toBeNull();
    expect(subject.releaseError()?.kind).toBe('not_found');
    expect(getRun).not.toHaveBeenCalled();
  });

  it('maps a forbidden read and any other failure to distinct region states', async () => {
    getRelease.mockRejectedValueOnce(refused('forbidden'));
    const subject = store();
    await subject.loadRelease('rel-1');
    expect(subject.releaseStatus()).toBe('forbidden');
    getRelease.mockRejectedValueOnce(refused('unavailable'));
    await subject.loadRelease('rel-1');
    expect(subject.releaseStatus()).toBe('error');
    expect(subject.releaseError()?.status).toBe(503);
  });
});
