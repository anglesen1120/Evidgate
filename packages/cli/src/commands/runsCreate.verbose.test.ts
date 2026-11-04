// `evidgate runs create --verbose`: the resolved config is dumped with the secret fields
// redacted, and neither the token nor the `Idempotency-Key` reaches a stream
// (contracts/cli.md §9).
import { describe, expect, it } from 'vitest';

import { devToken, mintedToken } from './devExchange.testing.js';
import { deriveRunIdempotencyKey } from './runsCreate.js';
import {
  manifestHash,
  manifestId,
  project,
  runId,
  apiCalls,
  capture,
  createArgv,
  created,
  invoke,
  stubDevFetch,
  useRunsCreateSpec,
} from './runsCreate.testing.js';

describe('runs create', () => {
  const { devEnv } = useRunsCreateSpec();

  // T78: `--verbose` dumps the resolved config with the secret fields
  // redacted, and the command adds no output of its own — neither the token nor the
  // `Idempotency-Key` it sent can appear.
  it('verbose_no_secrets', async () => {
    const env = await devEnv();
    const derivedKey = deriveRunIdempotencyKey({ manifestId, manifestHash });
    const stub = stubDevFetch(() => created());
    const out = capture();

    const argv = createArgv(['--verbose']);
    await expect(invoke(argv, env, out, stub.fetchImpl)).resolves.toBe(0);

    const text = out.stderr.join('');
    expect(text).toContain('evidgate: resolved config');
    expect(text).toContain('dev_token=[REDACTED]');
    expect(text).toContain(`project=${project}`);
    expect(text).not.toContain(devToken);
    expect(text).not.toContain(mintedToken);
    // The key did travel (the header is set) but never reaches either stream.
    expect(apiCalls(stub)[0]?.request.headers.get('idempotency-key')).toBe(derivedKey);
    expect(text).not.toContain(derivedKey);
    expect(out.stdout.join('')).not.toContain(derivedKey);
    expect(out.stdout.join('')).toBe(
      `run_id=${runId} manifest_hash=${manifestHash} state=queued\n`,
    );
  });
});
