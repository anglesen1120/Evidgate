// `evidgate runs create` failure surface: an invalid 2xx body and a non-envelope non-2xx are
// `request_failed` (never echoed), and a transport failure is `transport` (contracts/cli.md §5).
import { describe, expect, it } from 'vitest';

import {
  capture,
  createArgv,
  created,
  invoke,
  stubDevFetch,
  useRunsCreateSpec,
} from './runsCreate.testing.js';

describe('runs create', () => {
  const { devEnv } = useRunsCreateSpec();

  // §5: a 2xx with an invalid body (or a non-envelope non-2xx) is
  // `request_failed`, and the body is never echoed.
  it('malformed_body_request_failed', async () => {
    const env = await devEnv();

    const badId = stubDevFetch(() => created({ run_id: 'not-a-uuid' }));
    const badIdOut = capture();
    await expect(invoke(createArgv(), env, badIdOut, badId.fetchImpl)).resolves.toBe(3);
    const badIdText = badIdOut.stderr.join('');
    expect(badIdText).toContain('evidgate: error request_failed: ');
    expect(badIdText).not.toContain('not-a-uuid');
    expect(badIdOut.stdout.join('')).toBe('');

    const badState = stubDevFetch(() => created({ state: '' }));
    const badStateOut = capture();
    await expect(invoke(createArgv(), env, badStateOut, badState.fetchImpl)).resolves.toBe(3);
    expect(badStateOut.stderr.join('')).toContain('evidgate: error request_failed: ');
    expect(badStateOut.stdout.join('')).toBe('');

    // §9 step 3: the state must match `^[a-z][a-z0-9_]{0,31}$`. A newline, an ANSI escape,
    // an uppercase letter or a 33-character value is a bad body, and nothing from it — the
    // state included — reaches either stream.
    for (const state of [
      'queued\n',
      'queued\u001b[31m',
      'Queued',
      'queued state',
      '1queued',
      '_queued',
      'a'.repeat(33),
    ]) {
      const stub = stubDevFetch(() => created({ state }));
      const out = capture();
      await expect(invoke(createArgv(), env, out, stub.fetchImpl)).resolves.toBe(3);
      const text = out.stderr.join('');
      expect(text).toContain('evidgate: error request_failed: ');
      expect(text).toContain('the run create response was not valid');
      expect(text).not.toContain('queued');
      expect(text).not.toContain('\u001b');
      expect(out.stdout.join('')).toBe('');
    }

    // A gateway page that is not the 00 §9.2 envelope keeps the CLI's fallback code.
    const gateway = stubDevFetch(
      () =>
        new Response('<html>bad gateway</html>', {
          status: 502,
          headers: { 'content-type': 'text/html' },
        }),
    );
    const gatewayOut = capture();
    await expect(invoke(createArgv(), env, gatewayOut, gateway.fetchImpl)).resolves.toBe(3);
    const gatewayText = gatewayOut.stderr.join('');
    expect(gatewayText).toContain('evidgate: error request_failed: ');
    expect(gatewayText).toContain('HTTP 502');
    expect(gatewayText).not.toContain('bad gateway');
  });

  // §5: a transport failure is `transport` and never echoes the URL.
  it('transport_error_exit3', async () => {
    const env = await devEnv();
    // The exchange answers, so the failure is the create's own transport error.
    const failing = stubDevFetch(() => {
      throw new TypeError('fetch failed http://x');
    });
    const out = capture();

    await expect(invoke(createArgv(), env, out, failing.fetchImpl)).resolves.toBe(3);

    const text = out.stderr.join('');
    expect(text).toContain('evidgate: error transport: ');
    expect(text).not.toContain('http://x');
  });
});
