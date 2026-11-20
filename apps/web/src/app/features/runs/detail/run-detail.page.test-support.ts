// The run-detail page harness both of its specs share: the pinned run bodies, the data-service mocks
// a case drives, and the router navigation that mounts the page.
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import type { EvidenceRevisionPage, RunAttemptPage, RunStateDto } from '@evidgate/sdk';
import attemptsOk from '../../../../../../../fixtures/web-api/run-attempts/input.json';
import ready from '../../../../../../../fixtures/web-api/run-detail/input.json';
import formatted from '../../../../../../../fixtures/web-api/run-detail-ref-format/input.json';
import { vi } from 'vitest';
import { ReleasesDataService } from '../../../core/data/releases-data.service';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { DENY_CANCEL_SESSION } from '../actions/cancel/cancel-run.test-support';
import { routes } from '../runs.routes';
import { RunDataService } from '../data/run-data.service';
import { RunDetailPage } from './run-detail.page';
import { deferred } from './run-detail-store.fixtures';

// Both bodies are validated against the current RunStateDto by fixture-contract.spec.ts.
export const run = ready.body as RunStateDto;
export const refRun = formatted.body as RunStateDto;
export const attemptPage = attemptsOk.body as RunAttemptPage;

export function pendingBody() {
  return {
    status: 'pending',
    decision: null,
    evaluation_id: null,
    evidence_revision: null,
    completeness: null,
    outcome: null,
    missing: null,
    reasons: null,
    policy_hash: null,
    manifest_hash: null,
    evaluator_version: null,
    mode: null,
    url: null,
  };
}

export function liveBody() {
  return {
    ...pendingBody(),
    status: 'evaluated',
    decision: 'FAIL',
    evaluation_id: 'evaluation-1',
    evidence_revision: 4,
    completeness: 'incomplete',
    outcome: 'has_failures',
    missing: ['missing_shard'],
    reasons: ['critical_not_executed'],
    policy_hash: 'sha256:policy',
    manifest_hash: 'sha256:manifest',
    evaluator_version: '1.4.0',
    mode: 'live',
  };
}

export const get = vi.fn<(id: string) => Promise<RunStateDto>>();
export const listAttempts = vi.fn<(id: string, query?: object) => Promise<RunAttemptPage>>();
export const listEvidenceRevisions =
  vi.fn<(id: string, query?: object) => Promise<EvidenceRevisionPage>>();
export const getDecision = vi.fn<(id: string) => Promise<unknown>>();

/** Resets the mocks to their pinned answers and configures the page's providers. */
export function configureRunDetailTestBed(): void {
  get.mockReset().mockResolvedValue(run);
  listAttempts.mockReset().mockResolvedValue(attemptPage);
  listEvidenceRevisions.mockReset().mockResolvedValue({ items: [], next_cursor: null });
  getDecision.mockReset().mockResolvedValue(pendingBody());
  TestBed.configureTestingModule({
    providers: [
      DENY_CANCEL_SESSION,
      provideZonelessChangeDetection(),
      provideRouter([{ path: 'projects/:projectId/runs', children: routes }]),
      { provide: RunsDataService, useValue: { get, listAttempts, listEvidenceRevisions } },
      { provide: RunDataService, useValue: { getDecision } },
      { provide: ReleasesDataService, useValue: { get: vi.fn() } },
    ],
  });
}

export async function open(id = run.run_id, fragment = '', query = '') {
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(
    `/projects/project-1/runs/${id}${query}${fragment === '' ? '' : `#${fragment}`}`,
    RunDetailPage,
  );
  await settle(harness);
  return { harness, root: harness.routeNativeElement! };
}

/** One macrotask after the stable fixture drains the pin write-back and the render handoffs. */
export async function settle(harness: RouterTestingHarness): Promise<void> {
  await harness.fixture.whenStable();
  const tick = deferred<void>();
  setTimeout(tick.resolve, 0);
  await tick.promise;
  await harness.fixture.whenStable();
}
