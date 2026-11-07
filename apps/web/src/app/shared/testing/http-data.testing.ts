import type { HttpRequest } from '@angular/common/http';
import type { HttpTestingController, TestRequest } from '@angular/common/http/testing';

import { ApiRequestError, type ApiErrorKind } from '../../core/http/api-error';

/**
 * The served media type: the adapter reads bodies as text and `flush` does not derive it, while
 * the SDK picks its parser from the response `Content-Type` (`parseAs: 'auto'`), so a flush
 * without this header reaches the SDK as an unparsed stream.
 */
export const JSON_HEADERS = { 'Content-Type': 'application/json' } as const;

export interface TakeRequestProfile {
  /** `prefix` matches `request.url` from its start; `exact` matches `urlWithParams` whole. */
  readonly match: 'prefix' | 'exact';
  readonly attempts: number;
  /** `macrotask` also lets jsdom finish the blob parse a page read waits on. */
  readonly wait: 'macrotask' | 'microtask';
}

/**
 * Cursor pages: `url` stays the bare path while `urlWithParams` carries `?cursor=…`, and the next
 * page waits on jsdom's blob parse, which is a macrotask.
 */
export const PREFIX_MATCH_PROFILE: TakeRequestProfile = {
  match: 'prefix',
  attempts: 50,
  wait: 'macrotask',
};

/** One request per call: exact `urlWithParams`, microtasks only, so it runs under faked timers. */
export const EXACT_MATCH_PROFILE: TakeRequestProfile = {
  match: 'exact',
  attempts: 20,
  wait: 'microtask',
};

export type TakeRequest = (
  httpMock: HttpTestingController,
  match: string | ((request: HttpRequest<unknown>) => boolean),
) => Promise<TestRequest>;

/**
 * Drains the SDK -> adapter -> `HttpClient` pipeline until a request matches, so a spec can flush
 * it while `httpMock.verify()` still checks it.
 */
export function takeRequestWith(profile: TakeRequestProfile): TakeRequest {
  return async (httpMock, match) => {
    const matches = (candidate: HttpRequest<unknown>): boolean => {
      if (typeof match === 'function') {
        return match(candidate);
      }
      return profile.match === 'prefix'
        ? candidate.url.startsWith(match)
        : candidate.urlWithParams === match;
    };
    for (let attempt = 0; attempt < profile.attempts; attempt += 1) {
      const [request] = httpMock.match(matches);
      if (request !== undefined) {
        return request;
      }
      if (profile.wait === 'microtask') {
        await Promise.resolve();
      } else {
        await new Promise<void>((resolve) => setTimeout(resolve, 0));
      }
    }
    throw new Error('no HTTP request reached HttpTestingController');
  };
}

/** The cursor-page profile, which most data specs use. */
export const takeRequest = takeRequestWith(PREFIX_MATCH_PROFILE);

/** The typed refusal a fake data layer answers with, so a spec asserts state, not a shape. */
export function apiError(kind: ApiErrorKind, status: number): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: kind,
    message: `${kind} (test)`,
    requestId: null,
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  });
}
