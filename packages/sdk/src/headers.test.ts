// 01.3.2-T1.4: behavioural pin for the W5 header hooks — a hook only fills headers the
// request does not already carry, so a per-call `options.headers` value always wins over
// a provider (an interceptor that clobbered Idempotency-Key would break dedup).
import { describe, expect, it } from 'vitest';

import type { ResolvedRequestOptions } from '../gen/client/index.js';

import { createHeaderHookInterceptor } from './headers.js';

const requestOptions = (): ResolvedRequestOptions => ({
  headers: new Headers(),
  url: '/ci/token',
});

describe('createHeaderHookInterceptor', () => {
  it('sets the hook-provided headers on a bare request', async () => {
    const intercept = createHeaderHookInterceptor({
      idempotencyKey: () => 'idem-1',
      xRequestId: () => 'req-1',
      xsrfToken: () => 'xsrf-1',
    });
    const request = await intercept(
      new Request('http://localhost/ci/token', { method: 'POST' }),
      requestOptions(),
    );
    expect(request.headers.get('X-Request-Id')).toBe('req-1');
    expect(request.headers.get('X-XSRF-TOKEN')).toBe('xsrf-1');
    expect(request.headers.get('Idempotency-Key')).toBe('idem-1');
  });

  it('never overwrites a header the caller already set', async () => {
    const intercept = createHeaderHookInterceptor({
      idempotencyKey: () => 'from-hook',
      xRequestId: () => 'req-1',
    });
    const request = await intercept(
      new Request('http://localhost/ci/token', {
        method: 'POST',
        headers: { 'Idempotency-Key': 'explicit' },
      }),
      requestOptions(),
    );
    expect(request.headers.get('Idempotency-Key')).toBe('explicit');
    expect(request.headers.get('X-Request-Id')).toBe('req-1');
  });

  it('skips a header when its hook resolves empty', async () => {
    const intercept = createHeaderHookInterceptor({
      xsrfToken: () => null,
    });
    const request = await intercept(
      new Request('http://localhost/ci/token', { method: 'POST' }),
      requestOptions(),
    );
    expect(request.headers.has('X-XSRF-TOKEN')).toBe(false);
  });
});
