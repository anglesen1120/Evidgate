import { provideZonelessChangeDetection, signal } from '@angular/core';
import { InteractivityChecker } from '@angular/cdk/a11y';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import type {
  ApiTokenCreatedResponse,
  ApiTokenListResponse,
  ApiTokenView,
  CreateApiTokenRequest,
} from '@evidgate/sdk';
import { ApiTokensService } from '../../../core/data/api-tokens-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import type { Intent } from '../../../core/http/intent';
import { ApiTokensPage } from './api-tokens.page';
import { ApiTokensStore } from './api-tokens.store';

export const ROW: ApiTokenView = {
  id: 'row-1',
  name: 'nightly-reader',
  prefix: 'public-part',
  permissions: ['run:read'],
  status: 'active',
  expires_at: '2027-01-01T00:00:00Z',
  last_used_at: null,
  created_by: 'member-1',
};
export const CREATED: ApiTokenCreatedResponse = {
  id: 'created-1',
  name: 'Reader',
  prefix: 'new-prefix',
  permissions: ['run:read'],
  expires_at: '2027-01-01T00:00:00Z',
  token: 'one-time-test-credential',
};

export function refusal(kind: ApiErrorKind, seconds: number | null = null): ApiRequestError {
  return new ApiRequestError({
    kind,
    status: kind === 'forbidden' ? 403 : kind === 'rate_limited' ? 429 : 503,
    code: kind,
    message: 'The server refused this read.',
    requestId: 'request-1',
    retryable: kind !== 'forbidden',
    retryAfterSeconds: seconds,
    details: [],
  });
}

export function mount(
  answer: () => Promise<ApiTokenListResponse> = () =>
    Promise.resolve({ items: [ROW], next_cursor: null }),
) {
  const permissions = signal(new Set(['api_token:read', 'api_token:write', 'run:read']));
  const org = signal({ id: 'org-1' });
  const projects = signal([{ id: 'project-1', name: 'Storefront', org_id: 'org-1' }]);
  const currentProject = signal(projects()[0]);
  const endHandlers = new Set<() => void>();
  const session = {
    currentOrg: org,
    projects,
    currentProject,
    permissions,
    has: (code: string) => permissions().has(code),
    bootstrap: () => Promise.resolve(undefined),
    onSessionEnd: (callback: () => void) => {
      endHandlers.add(callback);
      return () => {
        endHandlers.delete(callback);
      };
    },
  };
  const list = vi.fn(answer);
  const create = vi
    .fn<
      (
        project: string,
        body: CreateApiTokenRequest,
        intent: Intent,
      ) => Promise<ApiTokenCreatedResponse>
    >()
    .mockResolvedValue(CREATED);
  const revoke = vi
    .fn<(id: string, intent: Intent) => Promise<void>>()
    .mockResolvedValue(undefined);
  TestBed.configureTestingModule({
    imports: [ApiTokensPage],
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: SessionStore, useValue: session },
      { provide: ApiTokensService, useValue: { list, create, revoke } },
    ],
  });
  vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
  const fixture = TestBed.createComponent(ApiTokensPage);
  const store = fixture.debugElement.injector.get(ApiTokensStore);
  const root = fixture.nativeElement as HTMLElement;
  async function settle(): Promise<void> {
    // A rendered, stable fixture is not proof an async write handler finished: the submit and
    // revoke continuations queue further microtasks after `whenStable()` answers. One macrotask
    // drains every microtask they queued before the final change detection runs.
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }
  const endSession = () => {
    for (const callback of endHandlers) callback();
    permissions.set(new Set());
  };
  return {
    fixture,
    store,
    root,
    session,
    permissions,
    projects,
    org,
    list,
    create,
    revoke,
    endSession,
    settle,
  };
}

/** `Promise.withResolvers` is ES2024; this project compiles against the ES2022 lib. */
export function deferred<T>(): {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason?: unknown) => void;
} {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}
