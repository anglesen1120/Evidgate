import type { RunPage, RunSummary } from '@evidgate/sdk';
import { ApiRequestError } from '../../../core/http/api-error';
import type { RunListQuery } from '../data/run-data.service';

/** One typed refusal, as `ApiGateway` builds it from a server envelope. */
function typedFailure(): ApiRequestError {
  return new ApiRequestError({
    kind: 'forbidden',
    status: 403,
    code: 'forbidden',
    message: 'The caller may not read this project run list.',
    requestId: 'req-runs-1',
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  });
}

/** One served run row; every case overrides only the fields it is about. */
function run(index: number, overrides: Partial<RunSummary> = {}): RunSummary {
  return {
    run_id: `run-${index}`,
    state: 'complete',
    created_at: new Date(Date.UTC(2026, 9, 1) + index * 60_000).toISOString(),
    ref: {
      kind: 'branch',
      name: `feature/run-${index}`,
      pr_number: null,
      head_sha: null,
      base_sha: null,
      trigger_event: 'push',
      is_fork: false,
    },
    unverified_ref: false,
    decision: null,
    completeness: 'complete',
    parent_run_id: null,
    rerun_scope: null,
    ...overrides,
  };
}

/** `count` rows, each one built by `build`. */
function rows(count: number, build: (index: number) => Partial<RunSummary> = () => ({})) {
  return Array.from({ length: count }, (_, index) => run(index, build(index)));
}

/**
 * Lets the store's pending continuations run, so a page the test resolved has been published (or a
 * follow-up request recorded) before the next assertion. One `await` per hop of the store's own
 * `await` chain; more turns than the chain has are harmless.
 */
async function flush(turns = 10): Promise<void> {
  for (let turn = 0; turn < turns; turn += 1) {
    await Promise.resolve();
  }
}

/**
 * The run pages the store reads. Cursors are opaque strings the fake mints itself, so a page can
 * hand out a next cursor the test controls exactly (a fresh index, the same one again, or none).
 */
class FakeRunDataService {
  /** Every row the fake can serve, in server order. */
  all: RunSummary[] = [];
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
  readonly pending: Array<{ readonly resolve: (page: RunPage) => void }> = [];
  /** Every call the store made, in order. */
  readonly calls: Array<{ readonly projectId: string; readonly request: RunListQuery }> = [];

  /** The index a minted cursor resumes at. */
  private readonly startByCursor = new Map<string, number>();

  listRuns(projectId: string, request: RunListQuery = {}): Promise<RunPage> {
    this.calls.push({ projectId, request });
    if (this.manual) {
      // The executor form: the app's ES2022 lib has no `Promise.withResolvers`.
      return new Promise<RunPage>((resolve) => {
        this.pending.push({ resolve });
      });
    }
    if (
      this.failure !== null &&
      (this.failOnCall === null || this.failOnCall === this.calls.length)
    ) {
      return Promise.reject(this.failure);
    }

    const start = request.cursor === undefined ? 0 : (this.startByCursor.get(request.cursor) ?? 0);
    const size = this.pageSizeOverride ?? request.limit ?? this.all.length;
    const page = this.all.slice(start, start + size);
    const items = page.filter(
      (row) =>
        request.decision === undefined ||
        (request.decision === 'pending'
          ? row.decision === null
          : row.decision === request.decision),
    );
    const end = start + page.length;
    let next: string | null = null;
    if (end < this.all.length) {
      next = this.repeatCursor ? 'same' : String(end);
      this.startByCursor.set(next, end);
    }
    return Promise.resolve({ items, next_cursor: next });
  }
}
export { typedFailure, run, rows, flush, FakeRunDataService };
