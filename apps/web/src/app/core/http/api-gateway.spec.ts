// 09.1.1-T8 (STATUS §6 rows tagged 09.1.1-T8): the gateway's classification edge cases, pinned
// without the network. `api-client.spec.ts` proves the same paths through the real SDK and
// `HttpClient`; these specs isolate one branch each, so a regression names itself:
//
// - a deadline `TimeoutError` (rejected by the operation, or handed back as `result.error`) is the
//   typed `timeout` failure, never a status mapping and never a success;
// - a rejection or `result.error` without an HTTP response is only a retryable `network`
//   failure when it is a `TypeError`; any other value is a programming error and is re-thrown
//   unchanged (a plain `Error` from `newUuid`, say, must not become a retryable network error);
// - an `AbortError` is re-thrown unchanged, even when the result also carries a response
//   (the abort arrived while the SDK was reading the body — nothing there is a success);
// - a refusal status is never a success value.

import { TestBed } from '@angular/core/testing';
import type { Client, ErrorEnvelope } from '@evidgate/sdk';

import { isApiRequestError } from './api-error';
import { API_CLIENT } from './api-client';
import { ApiGateway, type SdkResult } from './api-gateway';

const REQUEST_ID = 'req-t8-1';

const FORBIDDEN: ErrorEnvelope = {
  error: {
    code: 'forbidden',
    message: 'The request is not allowed.',
    request_id: REQUEST_ID,
    retryable: false,
  },
};

const PRECONDITION: ErrorEnvelope = {
  error: {
    code: 'precondition_failed',
    message: 'The row changed since it was read.',
    request_id: REQUEST_ID,
    retryable: false,
    details: [{ path: '/revision_id', reason: 'stale' }],
  },
};

const RATE_LIMITED: ErrorEnvelope = {
  error: {
    code: 'rate_limited',
    message: 'Too many requests.',
    request_id: REQUEST_ID,
    retryable: true,
  },
};

function envelopeResponse(
  status: number,
  envelope: ErrorEnvelope,
  headers: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify(envelope), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  });
}

/** Resolves with whatever a call that must not succeed rejected with. */
async function captureRejection(promise: Promise<unknown>): Promise<unknown> {
  try {
    await promise;
  } catch (cause) {
    return cause;
  }
  throw new Error('expected the call to fail, but it resolved');
}

describe('ApiGateway classification (09.1.1-T8)', () => {
  let gateway: ApiGateway;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [ApiGateway, { provide: API_CLIENT, useValue: {} as Client }],
    });
    gateway = TestBed.inject(ApiGateway);
  });

  describe('a deadline TimeoutError', () => {
    it('rejects with a typed timeout when the operation rejects with it', async () => {
      const timeout = new DOMException('The request timed out.', 'TimeoutError');
      const rejection = await captureRejection(
        gateway.call<SdkResult>(() => Promise.reject(timeout)),
      );

      if (!isApiRequestError(rejection)) {
        throw rejection;
      }
      expect(rejection.error.kind).toBe('timeout');
      expect(rejection.error.status).toBe(0);
      expect(rejection.error.code).toBe('request_timeout');
      expect(rejection.error.retryable).toBe(true);
      expect(rejection.error.retryAfterSeconds).toBeNull();
      expect(rejection.error.requestId).toBeNull();
      expect(rejection.cause).toBe(timeout);
    });

    it('rejects with a typed timeout when it arrives as result.error', async () => {
      const timeout = new DOMException('The request timed out.', 'TimeoutError');
      const rejection = await captureRejection(
        gateway.call<SdkResult>(() => Promise.resolve({ error: timeout })),
      );

      if (!isApiRequestError(rejection)) {
        throw rejection;
      }
      expect(rejection.error.kind).toBe('timeout');
      expect(rejection.error.retryable).toBe(true);
      expect(rejection.cause).toBe(timeout);
    });

    it('does not claim that the server never saw the request', async () => {
      const timeout = new DOMException('The request timed out.', 'TimeoutError');
      const rejection = await captureRejection(
        gateway.call<SdkResult>(() => Promise.reject(timeout)),
      );

      if (!isApiRequestError(rejection)) {
        throw rejection;
      }
      // A timeout says nothing about whether the server saw the request, so the message must not
      // claim that nothing happened (the only thing that makes a fresh key safe).
      expect(rejection.error.kind).not.toBe('network');
      expect(rejection.error.message).toContain('may');
    });
  });

  describe('an error without an HTTP response', () => {
    it('rethrows a non-TypeError error unchanged, not as retryable network', async () => {
      const bug = new Error('newUuid was given an empty namespace');
      const rejection = await captureRejection(
        gateway.call<SdkResult>(() => Promise.resolve({ error: bug })),
      );

      expect(rejection).toBe(bug);
      expect(isApiRequestError(rejection)).toBe(false);
    });

    it('rethrows a non-TypeError rejection of the operation unchanged', async () => {
      const bug = new RangeError('the operation misused the SDK');
      const rejection = await captureRejection(gateway.call<SdkResult>(() => Promise.reject(bug)));

      expect(rejection).toBe(bug);
    });

    it('maps a TypeError without a response to network, keeping it as the cause', async () => {
      const transportFailure = new TypeError('fetch failed');
      const rejection = await captureRejection(
        gateway.call<SdkResult>(() => Promise.resolve({ error: transportFailure })),
      );

      if (!isApiRequestError(rejection)) {
        throw rejection;
      }
      expect(rejection.error.kind).toBe('network');
      expect(rejection.error.status).toBe(0);
      expect(rejection.cause).toBe(transportFailure);
    });
  });

  describe('an AbortError', () => {
    it('rethrows the abort that arrived while the body was being read', async () => {
      const abort = new DOMException('The request was aborted.', 'AbortError');
      const rejection = await captureRejection(
        gateway.call<SdkResult>(() =>
          Promise.resolve({
            // A response arrived, but the SDK was still reading its body when the caller aborted:
            // the partial body is not a success value and is not an `unexpected` status either.
            response: new Response('{"items":[]}', {
              status: 200,
              headers: { 'Content-Type': 'application/json' },
            }),
            error: abort,
          }),
        ),
      );

      expect(rejection).toBe(abort);
      expect(isApiRequestError(rejection)).toBe(false);
    });

    it('rethrows an abort with no response and does not map it to network', async () => {
      const abort = new DOMException('The request was aborted.', 'AbortError');
      const rejection = await captureRejection(
        gateway.call<SdkResult>(() => Promise.resolve({ error: abort })),
      );

      expect(rejection).toBe(abort);
      expect(isApiRequestError(rejection)).toBe(false);
    });

    it('rethrows an abort rejected by the operation before any status mapping', async () => {
      const abort = new DOMException('The request was aborted.', 'AbortError');
      const rejection = await captureRejection(
        gateway.call<SdkResult>(() => Promise.reject(abort)),
      );

      expect(rejection).toBe(abort);
      expect(isApiRequestError(rejection)).toBe(false);
    });
  });

  describe('a refusal status is never a success value', () => {
    it.each([
      { status: 403, envelope: FORBIDDEN, kind: 'forbidden' },
      {
        status: 404,
        envelope: { error: { ...FORBIDDEN.error, code: 'not_found' } },
        kind: 'not_found',
      },
      { status: 412, envelope: PRECONDITION, kind: 'precondition_failed' },
      { status: 429, envelope: RATE_LIMITED, kind: 'rate_limited' },
    ])('$status $kind rejects with the typed failure', async ({ status, envelope, kind }) => {
      const rejection = await captureRejection(
        gateway.call<SdkResult>(() =>
          Promise.resolve({ response: envelopeResponse(status, envelope), error: envelope }),
        ),
      );

      if (!isApiRequestError(rejection)) {
        throw rejection;
      }
      expect(rejection.error.kind).toBe(kind);
      expect(rejection.error.status).toBe(status);
      expect(rejection.error.code).toBe(envelope.error.code);
      expect(rejection.error.requestId).toBe(REQUEST_ID);
    });

    it('throws precondition_failed with the details the dialog needs to reload', async () => {
      const rejection = await captureRejection(
        gateway.call<SdkResult>(() =>
          Promise.resolve({
            response: envelopeResponse(412, PRECONDITION),
            error: PRECONDITION,
          }),
        ),
      );

      if (!isApiRequestError(rejection)) {
        throw rejection;
      }
      expect(rejection.error.details).toHaveLength(1);
      expect(rejection.error.details[0]?.path).toBe('/revision_id');
    });

    it('echoes Retry-After in seconds on a 429', async () => {
      const rejection = await captureRejection(
        gateway.call<SdkResult>(() =>
          Promise.resolve({
            response: envelopeResponse(429, RATE_LIMITED, { 'Retry-After': '120' }),
            error: RATE_LIMITED,
          }),
        ),
      );

      if (!isApiRequestError(rejection)) {
        throw rejection;
      }
      expect(rejection.error.retryAfterSeconds).toBe(120);
    });
  });

  describe('a 2xx', () => {
    it('resolves the data of a success result', async () => {
      const value = await gateway.call<SdkResult>(() =>
        Promise.resolve({
          data: { items: [], next_cursor: null },
          response: new Response('{"items":[],"next_cursor":null}', {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        }),
      );

      expect(value).toEqual({ items: [], next_cursor: null });
    });

    it('resolves the empty data of a bodyless operation and never undefined', async () => {
      const value = await gateway.call<SdkResult>(() =>
        Promise.resolve({ data: {}, response: new Response(null, { status: 204 }) }),
      );

      expect(value).toEqual({});
    });

    it('never builds an ApiRequestError from a success result', async () => {
      const value = await gateway.call<SdkResult>(() =>
        Promise.resolve({ data: 'ok', response: new Response('ok', { status: 200 }) }),
      );

      expect(value).toBe('ok');
      expect(isApiRequestError(value)).toBe(false);
    });
  });
});
