// 09.3.1-T1 (plan 09.3.1 §2 AC9, §5 run-list row, §10 T1.1): the run list's request half.
//
// What this suite pins is the wire naming and the pass-through, because those are the two things the
// store relies on: `refName` reaches the API as `ref_name`, an absence is an absence (never
// `ref_name=`), the cursor and the limit are handed over untouched, and a typed refusal comes back
// exactly as `ApiGateway` classified it. The transport itself (interceptors, XSRF, status mapping)
// belongs to `core/data/runs-data.service.spec.ts` and is not retested here.

import { TestBed } from '@angular/core/testing';
import type { RunPage } from '@evidgate/sdk';

import { RunsDataService } from '../../../core/data/runs-data.service';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { RunDataService } from './run-data.service';

/** One typed refusal, as `ApiGateway` builds it from a server envelope. */
function typedFailure(): ApiRequestError {
  return new ApiRequestError({
    kind: 'validation',
    status: 422,
    code: 'request_invalid',
    message: 'ref_name is not a valid ref.',
    requestId: 'req-runs-1',
    retryable: false,
    details: [{ path: 'ref_name', reason: 'invalid_value' }],
    retryAfterSeconds: null,
  });
}

/** The `list` signature the feature service delegates to, faked so the wire query is visible. */
class FakeRunsDataService {
  readonly calls: Array<{
    readonly projectId: string;
    readonly query: { cursor?: string; limit?: number; ref_name?: string };
  }> = [];
  /** Rejects the next call with this value when set. */
  failure: Error | null = null;
  /** The page every call answers; `null` next cursor means the list ends. */
  page: RunPage = { items: [], next_cursor: null };

  list(projectId: string, query: { cursor?: string; limit?: number; ref_name?: string }) {
    this.calls.push({ projectId, query });
    if (this.failure !== null) {
      return Promise.reject(this.failure);
    }
    return Promise.resolve(this.page);
  }
}

describe('RunDataService (09.3.1-T1)', () => {
  let runs: FakeRunsDataService;
  let service: RunDataService;

  beforeEach(() => {
    runs = new FakeRunsDataService();
    TestBed.configureTestingModule({
      providers: [RunDataService, { provide: RunsDataService, useValue: runs }],
    });
    service = TestBed.inject(RunDataService);
  });

  it('sends nothing but the path when the request is empty', async () => {
    await service.listRuns('p-1');

    expect(runs.calls).toEqual([{ projectId: 'p-1', query: {} }]);
  });

  it('renames the UI filter to the contract member and keeps the page position', async () => {
    await service.listRuns('p-1', { cursor: 'cursor-2', limit: 50, refName: 'refs/heads/main' });

    expect(runs.calls).toEqual([
      {
        projectId: 'p-1',
        query: { cursor: 'cursor-2', limit: 50, ref_name: 'refs/heads/main' },
      },
    ]);
  });

  it('omits an empty or absent ref filter instead of sending ref_name=', async () => {
    await service.listRuns('p-1', { refName: '' });
    await service.listRuns('p-1', {});

    expect(runs.calls.map((call) => call.query)).toEqual([{}, {}]);
    for (const call of runs.calls) {
      expect(Object.hasOwn(call.query, 'ref_name')).toBe(false);
    }
  });

  it('omits an empty cursor, which is the first page', async () => {
    await service.listRuns('p-1', { cursor: '', limit: 50 });

    expect(runs.calls[0]?.query).toEqual({ limit: 50 });
  });

  it('returns the served page untouched', async () => {
    const page: RunPage = {
      items: [
        {
          run_id: 'r-1',
          state: 'complete',
          created_at: '2026-10-20T08:00:00Z',
          ref: {
            kind: 'branch',
            name: 'main',
            pr_number: null,
            head_sha: null,
            base_sha: null,
            trigger_event: 'push',
            is_fork: false,
          },
          unverified_ref: false,
          decision: 'PASS',
        },
      ],
      next_cursor: 'cursor-2',
    };
    runs.page = page;

    await expect(service.listRuns('p-1')).resolves.toBe(page);
  });

  it('propagates the typed refusal verbatim', async () => {
    const failure = typedFailure();
    runs.failure = failure;

    await expect(service.listRuns('p-1', { refName: 'bad ref' })).rejects.toBe(failure);
    const error: ApiError = failure.error;
    expect(error.code).toBe('request_invalid');
  });
});
