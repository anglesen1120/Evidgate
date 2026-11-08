// The run reads: the project run page and the by-id read that carries the evidence revision,
// with the 404 arm a foreign run id reaches.

import { HttpTestingController } from '@angular/common/http/testing';
import type { RunStateDto } from '@evidgate/sdk';

import { isApiRequestError } from '../http/api-error';
import {
  EXACT_MATCH_PROFILE,
  JSON_HEADERS,
  takeRequestWith,
} from '../../shared/testing/http-data.testing';
import { OnboardingDataService } from './onboarding-data.service';
import {
  API_BASE_URL,
  NO_RUNS,
  RUN_STATE,
  rejectionOf,
  setupOnboardingDataService,
} from './onboarding-data.service.test-support';

const takeRequest = takeRequestWith(EXACT_MATCH_PROFILE);

describe('OnboardingDataService (09.2.1-T1.1/T1.2)', () => {
  let httpMock: HttpTestingController;
  let service: OnboardingDataService;

  beforeEach(() => {
    ({ httpMock, service } = setupOnboardingDataService());
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('listRuns() reads the project run page', async () => {
    const runs = service.listRuns('p-1', { limit: 1 });
    const request = await takeRequest(httpMock, (candidate) =>
      candidate.url.startsWith(`${API_BASE_URL}/projects/p-1/runs`),
    );
    expect(request.request.method).toBe('GET');
    expect(request.request.urlWithParams).toContain('limit=1');
    request.flush(JSON.stringify(NO_RUNS), { headers: JSON_HEADERS });
    await expect(runs).resolves.toEqual(NO_RUNS);
  });

  it('getRun() reads one run, which carries the evidence revision', async () => {
    const run = service.getRun('r-1');
    const request = await takeRequest(httpMock, `${API_BASE_URL}/runs/r-1`);
    expect(request.request.method).toBe('GET');
    request.flush(JSON.stringify(RUN_STATE), { headers: JSON_HEADERS });
    const state: RunStateDto = await run;
    expect(state.current_evidence_revision_id).toBeNull();
  });

  it('getRun() surfaces a 404 envelope as a typed not_found error', async () => {
    const run = service.getRun('r-unknown');
    const envelope = {
      error: {
        code: 'not_found',
        message: 'Unknown run.',
        request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
        retryable: false,
      },
    };
    (await takeRequest(httpMock, `${API_BASE_URL}/runs/r-unknown`)).flush(
      JSON.stringify(envelope),
      { status: 404, statusText: '', headers: JSON_HEADERS },
    );

    const cause = await rejectionOf(run);
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('not_found');
    }
  });
});
