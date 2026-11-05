// The fixtures the Policy store's per-responsibility specs share: one page's ids and rows, the
// typed failures the adapter builds, and the fake data service each case drives. Every spec opens
// its own TestBed around them, so the fakes stay per test.

import type {
  ErrorEnvelope,
  PolicyActivationStateDto,
  PolicyRevisionDto,
  PolicyRevisionSummaryDto,
} from '@evidgate/sdk';
import type {
  PolicyHead,
  PolicyRevisionPage,
  PublishedRevision,
} from '../../../core/data/policy-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../../core/http/api-error';

export const PROJECT_ID = '018f0000-0000-7000-8000-00000000p001';
export const OTHER_PROJECT_ID = '018f0000-0000-7000-8000-00000000p002';
/** The revision the page opens; the list route is asked about this id. */
export const ANCHOR_ID = '018f0000-0000-7000-8000-0000000000a1';
/** The head the list answer's `ETag` names: the value a publish must quote. */
export const HEAD_ID = '018f0000-0000-7000-8000-0000000000b2';
/** A second revision, so `activeRevision` has something to move away from. */
export const SECOND_ID = '018f0000-0000-7000-8000-0000000000b3';
/** The activation in force on the project the anchor belongs to. */
export const ACTIVATION: PolicyActivationStateDto = {
  project_id: PROJECT_ID,
  policy_revision_id: HEAD_ID,
  hash: 'sha256:f250bbce48fca2a9a5f93b45236c0f330bb471f5d3f0d1396ddc5ac964928064',
  mode: 'advisory',
  activated_by: '018f0000-0000-7000-8000-0000000000e1',
  activated_at: '2026-10-26T08:22:00Z',
};

/** One list row, as the list route serves them (no body). */
export function summary(
  id: string,
  revisionNo: number,
  state: PolicyRevisionSummaryDto['state'],
): PolicyRevisionSummaryDto {
  return {
    id,
    revision_no: revisionNo,
    hash: `sha256:${revisionNo}`,
    state,
    created_by: 'u-1',
    created_at: '2026-10-20T06:05:00Z',
    version: 1,
  };
}

/** One stored revision, as `GET /v1/policies/{id}` serves it. */
export function revision(
  id: string,
  projectId: string,
  revisionNo: number,
  state: PolicyRevisionDto['state'],
): PolicyRevisionDto {
  return { ...summary(id, revisionNo, state), project_id: projectId, body: { flaky_budget: 1 } };
}

/** One served page: rows, the cursor after them, and the head the answer's `ETag` named. */
export function page(
  items: readonly PolicyRevisionSummaryDto[],
  nextCursor: string | null,
  headRevisionId: string | null,
): PolicyRevisionPage {
  return { items, nextCursor, headRevisionId };
}

/**
 * A JSON body of exactly `bytes` UTF-8 bytes. `{"a":"` is six bytes and `"}` is two, so the filler
 * carries the rest — which is how the 65 536/65 537 boundary is proved byte-exactly.
 */
export function bodyOfSize(bytes: number): string {
  return `{"a":"${'x'.repeat(bytes - 8)}"}`;
}

/** The typed failure `ApiGateway` builds from one envelope. */
export function typedFailure(
  envelope: ErrorEnvelope,
  kind: ApiErrorKind,
  status: number,
): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: envelope.error.code,
    message: envelope.error.message,
    requestId: envelope.error.request_id,
    retryable: envelope.error.retryable,
    details: envelope.error.details ?? [],
    retryAfterSeconds: null,
  });
}

/** One deferred answer, so a test controls exactly when a read settles. */
export type Deferred<T> = {
  readonly resolve: (value: T) => void;
  readonly reject: (error: unknown) => void;
};

/**
 * The reads the store performs. Cursor `"n"` names `pages[n]`, so a page can hand out a next cursor
 * the test controls exactly; the first page (no cursor) is `pages[0]`.
 *
 * The project's lib target is ES2022, so `Promise.withResolvers` is not available: the executor form
 * is how a deferred resolve/reject handle escapes to the test (the same spelling
 * `http-client-fetch.ts` uses).
 */
export class FakePolicyDataService {
  readonly revisions = new Map<string, PolicyRevisionDto>();
  pages: readonly PolicyRevisionPage[] = [];
  readonly getCalls: string[] = [];
  readonly listCalls: Array<{ revisionId: string; cursor: string | undefined }> = [];
  readonly publishCalls: Array<{ projectId: string; body: unknown; ifMatch: string | null }> = [];
  getFailure: Error | null = null;
  listFailure: Error | null = null;
  publishFailure: Error | null = null;
  publishAnswer: PublishedRevision | null = null;
  /** When true, every page names a fresh cursor, so the store's page cap is the only stop. */
  endless = false;
  getManual = false;
  listManual = false;
  publishManual = false;
  readonly pendingGets: Deferred<PolicyRevisionDto>[] = [];
  readonly pendingLists: Deferred<PolicyRevisionPage>[] = [];
  readonly pendingPublishes: Deferred<PublishedRevision>[] = [];

  getRevision(revisionId: string): Promise<PolicyRevisionDto> {
    this.getCalls.push(revisionId);
    if (this.getManual) {
      return new Promise<PolicyRevisionDto>((resolve, reject) => {
        this.pendingGets.push({ resolve, reject });
      });
    }
    if (this.getFailure !== null) {
      return Promise.reject(this.getFailure);
    }
    const stored = this.revisions.get(revisionId);
    return stored === undefined
      ? Promise.reject(new Error(`the fake holds no revision ${revisionId}`))
      : Promise.resolve(stored);
  }

  listRevisions(revisionId: string, cursor?: string): Promise<PolicyRevisionPage> {
    this.listCalls.push({ revisionId, cursor });
    if (this.listManual) {
      return new Promise<PolicyRevisionPage>((resolve, reject) => {
        this.pendingLists.push({ resolve, reject });
      });
    }
    if (this.listFailure !== null) {
      return Promise.reject(this.listFailure);
    }
    if (this.endless) {
      return Promise.resolve({
        items: [],
        nextCursor: `c-${this.listCalls.length}`,
        headRevisionId: HEAD_ID,
      });
    }
    return Promise.resolve(
      this.pages[cursor === undefined ? 0 : Number(cursor)] ?? page([], null, null),
    );
  }

  publish(projectId: string, body: unknown, ifMatch: string | null): Promise<PublishedRevision> {
    this.publishCalls.push({ projectId, body, ifMatch });
    if (this.publishManual) {
      return new Promise<PublishedRevision>((resolve, reject) => {
        this.pendingPublishes.push({ resolve, reject });
      });
    }
    if (this.publishFailure !== null) {
      return Promise.reject(this.publishFailure);
    }
    if (this.publishAnswer === null) {
      return Promise.reject(new Error('the fake holds no publish answer'));
    }
    return Promise.resolve(this.publishAnswer);
  }

  /** The project reads the tab performs beside the list, each answer under the test's control. */
  readonly activationCalls: string[] = [];
  readonly headCalls: string[] = [];
  activation: Promise<PolicyActivationStateDto> = Promise.resolve(ACTIVATION);
  head: Promise<PolicyHead> = Promise.resolve({
    revision: summary(HEAD_ID, 3, 'active'),
    headRevisionId: HEAD_ID,
  });

  getActivation(projectId: string): Promise<PolicyActivationStateDto> {
    this.activationCalls.push(projectId);
    return this.activation;
  }

  getHead(projectId: string): Promise<PolicyHead> {
    this.headCalls.push(projectId);
    return this.head;
  }

  /** Answers the n-th deferred revision read the way the server would. */
  settleGet(index: number, stored: PolicyRevisionDto): void {
    const pending = this.pendingGets[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending revision read ${index}`);
    }
    pending.resolve(stored);
  }

  /** Settles a deferred revision read with whatever the server (or the adapter) threw. */
  failGet(index: number, error: unknown): void {
    const pending = this.pendingGets[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending revision read ${index}`);
    }
    pending.reject(error);
  }

  /** Answers the n-th deferred list read the way the server would. */
  settleList(index: number, served: PolicyRevisionPage): void {
    const pending = this.pendingLists[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending list read ${index}`);
    }
    pending.resolve(served);
  }

  /** Settles a deferred list read with whatever the server (or the adapter) threw. */
  failList(index: number, error: unknown): void {
    const pending = this.pendingLists[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending list read ${index}`);
    }
    pending.reject(error);
  }

  /** Answers the n-th deferred publish with the server's stored revision. */
  settlePublish(index: number, stored: PublishedRevision): void {
    const pending = this.pendingPublishes[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending publish ${index}`);
    }
    pending.resolve(stored);
  }
}

/**
 * The store's session dependencies here: which projects the caller is in, and the permission codes
 * it holds. `policy:read` is what the tab itself needs; `policy:write` is what a publish needs, and it
 * is present by default so a case about publishing does not have to say so.
 */
export class FakeSessionStore {
  rows: readonly { readonly id: string }[] = [{ id: PROJECT_ID }];
  codes: ReadonlySet<string> = new Set(['policy:read', 'policy:write']);

  projects(): readonly { readonly id: string }[] {
    return this.rows;
  }

  has(code: string): boolean {
    return this.codes.has(code);
  }
}

/**
 * The publish's refresh and every other background write are not awaited by their caller, so a test
 * has to let the queued microtasks run before it can see what the read wrote. Four ticks cover the
 * `settledRead` hop plus the read's own `await`; nothing here uses timers, so this is deterministic.
 */
export async function flushMicrotasks(): Promise<void> {
  for (let tick = 0; tick < 4; tick += 1) {
    await Promise.resolve();
  }
}
