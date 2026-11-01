import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { EvidenceRevisionPage, RunAttemptPage, RunStateDto } from '@evidgate/sdk';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { RunDataService } from '../data/run-data.service';
import { RunDetailStore } from './run-detail.store';
import {
  deferred,
  liveBody,
  pendingBody,
  refused,
  revision,
  revisionPage,
  run,
} from './run-detail-store.fixtures';

describe('RunDetailStore revisions', () => {
  const get = vi.fn<(id: string) => Promise<RunStateDto>>();
  const listAttempts =
    vi.fn<(id: string, query?: { cursor?: string; limit?: number }) => Promise<RunAttemptPage>>();
  const listEvidenceRevisions =
    vi.fn<
      (id: string, query?: { cursor?: string; limit?: number }) => Promise<EvidenceRevisionPage>
    >();
  const getDecision = vi.fn<(id: string) => Promise<unknown>>();

  beforeEach(() => {
    get.mockReset().mockResolvedValue(run);
    listAttempts.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    listEvidenceRevisions.mockReset().mockResolvedValue(revisionPage([]));
    getDecision.mockReset().mockResolvedValue(pendingBody());
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        RunDetailStore,
        { provide: RunsDataService, useValue: { get, listAttempts, listEvidenceRevisions } },
        { provide: RunDataService, useValue: { getDecision } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  function store(): InstanceType<typeof RunDetailStore> {
    return TestBed.inject(RunDetailStore);
  }

  it('keeps the served newest-first order and pins the accepted decision revision', async () => {
    const newest = revision({ revision_no: 4, evaluation_id: 'evaluation-4' });
    const older = revision({ revision_no: 3 });
    listEvidenceRevisions.mockResolvedValue(revisionPage([newest, older]));
    getDecision.mockResolvedValue(liveBody({ evidence_revision: 4 }));
    const subject = store();
    await subject.load(run.run_id);
    expect(listEvidenceRevisions).toHaveBeenCalledExactlyOnceWith(run.run_id, { limit: 200 });
    expect(subject.revisions()?.map((row) => row.revision_no)).toEqual([4, 3]);
    expect(subject.pinnedRevision()).toBe(4);
    expect(subject.selectedRevision()).toBe(newest);
    expect(subject.newerRevisionExists()).toBe(false);
    expect(subject.latestRevisionCompleteness()).toBe('incomplete');
  });

  it('pins the newest frozen revision when the decision is only pending', async () => {
    listEvidenceRevisions.mockResolvedValue(
      revisionPage([revision({ revision_no: 3 }), revision({ revision_no: 2 })]),
    );
    const subject = store();
    await subject.load(run.run_id);
    expect(subject.decision()).toEqual({ kind: 'pending' });
    expect(subject.pinnedRevision()).toBe(3);
    expect(subject.selectedRevision()?.revision_no).toBe(3);
  });

  it('an explicit URL pin wins over the decision revision, even when the decision lands later', async () => {
    const decision = deferred<unknown>();
    getDecision.mockReturnValue(decision.promise);
    listEvidenceRevisions.mockResolvedValue(
      revisionPage([revision({ revision_no: 4 }), revision({ revision_no: 3 })]),
    );
    const subject = store();
    const loading = subject.load(run.run_id);
    subject.applyUrlPin(3);
    decision.resolve(liveBody({ evidence_revision: 4 }));
    await loading;
    expect(subject.pinnedRevision()).toBe(3);
    expect(subject.selectedRevision()?.revision_no).toBe(3);
    expect(subject.newerRevisionExists()).toBe(true);
  });

  it('never auto-switches a default pin when a slower decision names another revision', async () => {
    listEvidenceRevisions.mockResolvedValue(revisionPage([revision({ revision_no: 3 })]));
    const subject = store();
    await subject.load(run.run_id);
    expect(subject.pinnedRevision()).toBe(3);
    getDecision.mockResolvedValue(liveBody({ evidence_revision: 4 }));
    await subject.refreshDecision();
    expect(subject.pinnedRevision()).toBe(3);
    // The decision now names revision 4, which is not on the loaded list, so no banner is invented
    // for a revision this page has not seen.
    expect(subject.decision()).toMatchObject({ kind: 'evaluated', evidenceRevision: 4 });
    expect(subject.selectedRevision()?.revision_no).toBe(3);
    expect(subject.newerRevisionExists()).toBe(false);
  });

  it('a failed revision read leaves the run, decision and attempts intact and retries alone', async () => {
    listEvidenceRevisions.mockRejectedValueOnce(refused('unavailable'));
    getDecision.mockResolvedValue(liveBody({ evidence_revision: 4 }));
    const subject = store();
    await subject.load(run.run_id);
    expect(subject.status()).toBe('ready');
    expect(subject.run()).toEqual(run);
    expect(subject.decision()).toMatchObject({ kind: 'evaluated', decision: 'FAIL' });
    expect(subject.revisionsStatus()).toBe('error');
    expect(subject.revisions()).toBeNull();
    expect(subject.revisionsError()).not.toBeNull();
    // The decision still pins its own revision, so the panel can show that revision's body.
    expect(subject.pinnedRevision()).toBe(4);

    listEvidenceRevisions.mockResolvedValue(
      revisionPage([revision({ revision_no: 4 }), revision({ revision_no: 3 })]),
    );
    await subject.retryRevisions();
    expect(listEvidenceRevisions).toHaveBeenCalledTimes(2);
    expect(subject.revisionsStatus()).toBe('ready');
    expect(subject.revisions()).toHaveLength(2);
    expect(subject.pinnedRevision()).toBe(4);
  });

  it('loads older pages by cursor and resolves a pin that was not on the first page', async () => {
    listEvidenceRevisions
      .mockResolvedValueOnce(revisionPage([revision({ revision_no: 5 })], 'cursor-1'))
      .mockResolvedValueOnce(
        revisionPage([revision({ revision_no: 4 }), revision({ revision_no: 3 })], null),
      );
    const subject = store();
    await subject.load(run.run_id);
    expect(subject.pinnedRevision()).toBe(5);
    expect(subject.hasMoreRevisions()).toBe(true);

    subject.applyUrlPin(3);
    expect(subject.selectedRevision()).toBeNull();
    expect(subject.hasMoreRevisions()).toBe(true);

    await subject.loadOlderRevisions();
    expect(listEvidenceRevisions).toHaveBeenLastCalledWith(run.run_id, {
      limit: 200,
      cursor: 'cursor-1',
    });
    expect(subject.revisions()?.map((row) => row.revision_no)).toEqual([5, 4, 3]);
    expect(subject.hasMoreRevisions()).toBe(false);
    expect(subject.selectedRevision()?.revision_no).toBe(3);
  });

  it('reports a pin that no page carries only after the walk is exhausted', async () => {
    listEvidenceRevisions.mockResolvedValue(revisionPage([revision({ revision_no: 4 })]));
    getDecision.mockResolvedValue(liveBody({ evidence_revision: 4 }));
    const subject = store();
    const loading = subject.load(run.run_id);
    subject.applyUrlPin(9);
    await loading;
    expect(subject.pinnedRevision()).toBe(9);
    expect(subject.selectedRevision()).toBeNull();
    expect(subject.hasMoreRevisions()).toBe(false);
    expect(subject.decision()).toMatchObject({ kind: 'evaluated', evidenceRevision: 4 });
  });

  it('refuses a repeated cursor as a typed failure and keeps the loaded options', async () => {
    listEvidenceRevisions
      .mockResolvedValueOnce(revisionPage([revision({ revision_no: 4 })], 'cursor-1'))
      .mockResolvedValueOnce(revisionPage([revision({ revision_no: 3 })], 'cursor-1'));
    const subject = store();
    await subject.load(run.run_id);
    await subject.loadOlderRevisions();
    expect(subject.revisionsStatus()).toBe('ready');
    expect(subject.revisions()?.map((row) => row.revision_no)).toEqual([4]);
    expect(subject.revisionsError()?.code).toBe('revisions_cursor_repeated');
    expect(subject.hasMoreRevisions()).toBe(false);
  });

  it('removing the URL pin re-derives the decision revision once', async () => {
    listEvidenceRevisions.mockResolvedValue(
      revisionPage([revision({ revision_no: 4 }), revision({ revision_no: 3 })]),
    );
    getDecision.mockResolvedValue(liveBody({ evidence_revision: 4 }));
    const subject = store();
    await subject.load(run.run_id);
    subject.applyUrlPin(3);
    expect(subject.pinnedRevision()).toBe(3);
    subject.applyUrlPin(null);
    expect(subject.pinnedRevision()).toBe(4);
    expect(subject.selectedRevision()?.revision_no).toBe(4);
  });

  it('ignores a pin that is not a positive integer revision number', async () => {
    listEvidenceRevisions.mockResolvedValue(revisionPage([revision({ revision_no: 4 })]));
    const subject = store();
    await subject.load(run.run_id);
    const pinned = subject.pinnedRevision();
    subject.applyUrlPin(0);
    subject.pinRevision(-2);
    expect(subject.pinnedRevision()).toBe(pinned);
  });

  it('replaces the revision region on a route change instead of keeping the old rows', async () => {
    listEvidenceRevisions.mockResolvedValueOnce(
      revisionPage([revision({ revision_no: 7, evaluation_id: 'evaluation-7' })]),
    );
    const subject = store();
    await subject.load(run.run_id);
    expect(subject.revisions()).toHaveLength(1);
    listEvidenceRevisions.mockResolvedValueOnce(
      revisionPage([revision({ revision_no: 1, evaluation_id: null })]),
    );
    await subject.load('run-2');
    expect(subject.revisions()?.map((row) => row.revision_no)).toEqual([1]);
    expect(subject.selectedRevision()?.revision_no).toBe(1);
  });

  it('a late revision answer for the previous run never lands on the new one', async () => {
    const oldRead = deferred<EvidenceRevisionPage>();
    listEvidenceRevisions.mockReturnValueOnce(oldRead.promise);
    const subject = store();
    const first = subject.load('run-old');
    listEvidenceRevisions.mockResolvedValueOnce(revisionPage([revision({ revision_no: 2 })]));
    const second = subject.load('run-new');
    oldRead.resolve(revisionPage([revision({ revision_no: 99 })]));
    await Promise.all([first, second]);
    expect(subject.revisions()?.map((row) => row.revision_no)).toEqual([2]);
  });
});
