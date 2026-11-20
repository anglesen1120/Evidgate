// The transport-wide cap meeting `gate`'s own readers: an answer that declares a length over
// the cap is one fatal refusal, not a transport failure the poll loop replays.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import { devStubFetch } from './devExchange.testing.js';
import { cacheDirs, capture, devEnv, gateArgv, invoke, runId } from './gate.kit.testing.js';

describe('gate response cap', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
  });

  it.each([200, 503])(
    'ends on response_too_large after one request for a declared oversize %i body',
    async (status) => {
      const env = await devEnv(dirs);
      const stub = devStubFetch(
        () =>
          new Response('{}', {
            status,
            headers: { 'content-type': 'application/json', 'content-length': '2000000' },
          }),
      );
      const out = capture();

      const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

      expect(code).toBe(3);
      expect(stub.calls).toHaveLength(1);
      expect(out.stdout.join('')).toBe('');
      expect(out.stderr.join('')).toContain('error response_too_large:');
    },
  );

  it('refuses a status Response cannot represent after one request, without a replay', async () => {
    const env = await devEnv(dirs);
    let cancelled = false;
    const stub = devStubFetch(() => {
      const body = new ReadableStream<Uint8Array>({
        cancel() {
          cancelled = true;
        },
      });
      return Object.defineProperty(new Response(body), 'status', { value: 600 });
    });
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(cancelled).toBe(true);
    expect(out.stderr.join('')).toContain('error request_failed:');
  });
});
