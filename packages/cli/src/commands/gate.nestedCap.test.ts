// A decision body read through two bounded readers: the shared 1 MiB wrapper under the command's
// own 256 KiB one. A single chunk past the inner cap must end as one fatal `response_too_large`,
// never as the retryable read failure the outer reader reports for a broken stream.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import { devStubFetch } from './devExchange.testing.js';
import {
  cacheDirs,
  capture,
  decided,
  devEnv,
  evaluated,
  gateArgv,
  invoke,
  runId,
} from './gate.kit.testing.js';

/**
 * A 200 with no `Content-Length` whose first chunk is past the 1 MiB transport cap and whose
 * second is a valid PASS verdict, so reading on would end as a pass instead of a refusal.
 */
function oversizeThenVerdict(): Response {
  const chunks = [
    new Uint8Array(1024 * 1024 + 1).fill(0x20),
    new TextEncoder().encode(JSON.stringify(evaluated({ decision: 'PASS' }))),
  ];
  let index = 0;
  return new Response(
    new ReadableStream<Uint8Array>({
      pull(controller) {
        const chunk = chunks[index];
        index += 1;
        if (chunk === undefined) {
          controller.close();
          return;
        }
        controller.enqueue(chunk);
      },
    }),
    { headers: { 'content-type': 'application/json' } },
  );
}

describe('gate with an oversize inner chunk', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
  });

  it('ends on response_too_large after one request instead of retrying the cut answer', async () => {
    const env = await devEnv(dirs);
    // A retry would be answered with a valid verdict, so the exit code tells the paths apart.
    const stub = devStubFetch((_request, index) =>
      index === 0 ? oversizeThenVerdict() : decided(evaluated({ decision: 'PASS' })),
    );
    const out = capture();

    const { code, clock } = await invoke(
      gateArgv(['--run', runId, '--wait', '5s']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(clock.sleeps).toEqual([]);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toContain('error response_too_large:');
  });
});
