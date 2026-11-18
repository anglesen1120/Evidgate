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
import type { CreatedRun } from './run-actions.types';
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
  /** The value the decision read answers, served by the gateway untyped. */
  decision: unknown = null;

  list(projectId: string, query: { cursor?: string; limit?: number; ref_name?: string }) {
    this.calls.push({ projectId, query });
    if (this.failure !== null) {
      return Promise.reject(this.failure);
    }
    return Promise.resolve(this.page);
  }

  getDecision(runId: string): Promise<unknown> {
    this.decisionCalls.push(runId);
    if (this.failure !== null) return Promise.reject(this.failure);
    return Promise.resolve(this.decision);
  }

  readonly decisionCalls: string[] = [];

  /** The create signature the feature service delegates to, faked so its arguments are visible. */
  readonly createCalls: Array<{
    readonly projectId: string;
    readonly body: unknown;
    readonly key: string;
    readonly signal: AbortSignal | undefined;
  }> = [];

  /** The cancel signature, faked for the same reason. */
  readonly cancelCalls: Array<{
    readonly runId: string;
    readonly reason: string | undefined;
    readonly key: string;
    readonly signal: AbortSignal | undefined;
  }> = [];

  /** The 201 body a create answers. */
  created: CreatedRun = {
    run_id: '018f0000-0000-7000-8000-000000000021',
    state: 'queued',
    unverified_ref: false,
  };

  create(
    projectId: string,
    body: unknown,
    intent: { readonly idempotencyKey: string },
    signal?: AbortSignal,
  ): Promise<CreatedRun> {
    this.createCalls.push({ projectId, body, key: intent.idempotencyKey, signal });
    return this.failure !== null ? Promise.reject(this.failure) : Promise.resolve(this.created);
  }

  cancel(
    runId: string,
    reason: string | undefined,
    intent: { readonly idempotencyKey: string },
    signal?: AbortSignal,
  ): Promise<void> {
    this.cancelCalls.push({ runId, reason, key: intent.idempotencyKey, signal });
    return this.failure !== null ? Promise.reject(this.failure) : Promise.resolve();
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

  it.each([
    ['pending', 'pending'],
    ['PASS', 'pass'],
    ['FAIL', 'fail'],
    ['INCONCLUSIVE', 'inconclusive'],
  ] as const)(
    'maps the %s UI decision to the %s wire filter with ref and cursor',
    async (decision, wireDecision) => {
      await service.listRuns('p-1', { decision, refName: 'main', cursor: 'next', limit: 50 });
      expect(runs.calls[0]?.query).toEqual({
        decision: wireDecision,
        ref_name: 'main',
        cursor: 'next',
        limit: 50,
      });
    },
  );

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
          completeness: 'complete',
          parent_run_id: null,
          rerun_scope: null,
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

  it('returns a served decision body once its shape is valid', async () => {
    const body = {
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
    runs.decision = body;

    await expect(service.getDecision('r-1')).resolves.toStrictEqual(body);
    expect(runs.decisionCalls).toEqual(['r-1']);
  });

  it('answers null for a served value that is not a decision body', async () => {
    runs.decision = { status: 'evaluated', decision: 'SKIPPED' };

    await expect(service.getDecision('r-1')).resolves.toBeNull();
  });

  it('propagates a decision refusal verbatim', async () => {
    const failure = typedFailure();
    runs.failure = failure;

    await expect(service.getDecision('r-1')).rejects.toBe(failure);
  });

  it('createRun hands the project, body and caller key over and answers the served run', async () => {
    const body = { manifest_id: '018f0000-0000-7000-8000-000000000011' };

    await expect(service.createRun('p-1', body, 'intent-1')).resolves.toBe(runs.created);
    expect(runs.createCalls).toEqual([
      { projectId: 'p-1', body, key: 'intent-1', signal: undefined },
    ]);
  });

  it('keeps one key across two attempts of one intent and sends the next key for a new one', async () => {
    const body = { manifest_id: '018f0000-0000-7000-8000-000000000011' };
    const first = service.newIdempotencyKey();
    const second = service.newIdempotencyKey();

    await service.createRun('p-1', body, first);
    await service.createRun('p-1', body, first);
    await service.createRun('p-1', body, second);

    expect(runs.createCalls.map((call) => call.key)).toEqual([first, first, second]);
    expect(first).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    expect(second).not.toBe(first);
  });

  it('forwards the abort signal of a create so a dismissed dialog cancels the call', async () => {
    const controller = new AbortController();

    await service.createRun(
      'p-1',
      { manifest_id: '018f0000-0000-7000-8000-000000000011' },
      'intent-1',
      controller.signal,
    );

    expect(runs.createCalls[0]?.signal).toBe(controller.signal);
  });

  it('propagates a create refusal verbatim, idempotency conflict included', async () => {
    const failure = new ApiRequestError({
      kind: 'idempotency_conflict',
      status: 409,
      code: 'idempotency_conflict',
      message: 'The key was used for another body.',
      requestId: 'req-create-1',
      retryable: false,
      details: [],
      retryAfterSeconds: null,
    });
    runs.failure = failure;

    await expect(
      service.createRun('p-1', { manifest_id: '018f0000-0000-7000-8000-000000000011' }, 'intent-1'),
    ).rejects.toBe(failure);
  });

  it.each([undefined, 'The wrong manifest was selected.'])(
    'cancelRun sends the reason %s with its key and answers undefined',
    async (reason) => {
      const controller = new AbortController();

      await expect(
        service.cancelRun('r-1', reason, 'intent-1', controller.signal),
      ).resolves.toBeUndefined();
      expect(runs.cancelCalls).toEqual([
        { runId: 'r-1', reason, key: 'intent-1', signal: controller.signal },
      ]);
    },
  );

  it('propagates a cancel refusal verbatim', async () => {
    const failure = typedFailure();
    runs.failure = failure;

    await expect(service.cancelRun('r-1', undefined, 'intent-1')).rejects.toBe(failure);
  });
});
