import { provideZonelessChangeDetection, signal } from '@angular/core';
import { InteractivityChecker } from '@angular/cdk/a11y';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, of } from 'rxjs';
import { vi } from 'vitest';
import type {
  ApiTokenCreatedResponse,
  ApiTokenListResponse,
  ApiTokenView,
  CreateApiTokenRequest,
  ProjectDto,
} from '@evidgate/sdk';
import { ApiTokensService } from '../../../core/data/api-tokens-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import type { Intent } from '../../../core/http/intent';
import { ApiTokensPage } from './api-tokens.page';
import { ApiTokensStore } from './api-tokens.store';

/** A session project; only the fields the picker renders vary between fixtures. */
export function project(id: string, name = 'Storefront'): ProjectDto {
  return {
    id,
    org_id: 'org-1',
    name,
    slug: id,
    status: 'active',
    default_environment_id: null,
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z',
    version: 1,
    retention_days: null,
    monthly_run_quota: null,
    case_prefix: 'STOR',
  };
}

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
  options: {
    query?: Record<string, string>;
    projects?: readonly ProjectDto[];
  } = {},
) {
  const permissions = signal(new Set(['api_token:read', 'api_token:write', 'run:read']));
  // The org the URL names above the page; a spec that moves the session to another org is a
  // foreign scope, which is exactly what the URL guard has to notice.
  const activatedOrg = 'org-1';
  const org = signal({ id: activatedOrg });
  const projects = signal<ProjectDto[]>([...(options.projects ?? [project('project-1')])]);
  const currentProject = signal(projects()[0]);
  const query = new BehaviorSubject(convertToParamMap(options.query ?? {}));
  const navigate = vi.fn<Router['navigate']>().mockResolvedValue(true);
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
  const list =
    vi.fn<
      (
        project: string,
        cursor: string | undefined,
        abort: AbortSignal,
      ) => Promise<ApiTokenListResponse>
    >(answer);
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
      // The page and its store read the URL through one stub so a spec can seed a deep link; the
      // param chain carries the org the URL above the page names.
      {
        provide: ActivatedRoute,
        useValue: {
          queryParamMap: query,
          pathFromRoot: [{ paramMap: of(convertToParamMap({ orgId: activatedOrg })) }],
        },
      },
      { provide: SessionStore, useValue: session },
      { provide: ApiTokensService, useValue: { list, create, revoke } },
    ],
  });
  vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
  const router = TestBed.inject(Router);
  vi.spyOn(router, 'navigate').mockImplementation((commands, extras) => navigate(commands, extras));
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
    query,
    navigate,
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
