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
});
