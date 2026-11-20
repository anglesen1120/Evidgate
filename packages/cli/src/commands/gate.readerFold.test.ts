// The bounded decision read stood in for here: the four mappings a `gate` run depends on, each
// driven through the command so a body either parses, is retried as a cut, or is refused.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import { devStubFetch } from './devExchange.testing.js';
import {
  cacheDirs,
  capture,
  devEnv,
  evaluated,
  gateArgv,
  invoke,
  runId,
} from './gate.kit.testing.js';

/** A 200 whose body is `text`, optionally broken after that one chunk arrives. */
function streamingAnswer(text: string, broken = false): Response {
  let sent = false;
  return new Response(
    new ReadableStream<Uint8Array>({
      pull(controller) {
        if (!sent) {
          sent = true;
          controller.enqueue(new TextEncoder().encode(text));
        } else if (broken) {
          controller.error(new Error('private socket failure detail'));
        } else {
          controller.close();
        }
      },
    }),
    { headers: { 'content-type': 'application/json' } },
  );
}

const readerCases = [
  {
    name: 'retries a cut stream as a transient read failure and never reports its verdict',
    response: () => streamingAnswer(JSON.stringify(evaluated({ decision: 'PASS' })), true),
    code: 3,
    requests: 2,
    sleeps: [1000],
    stdout: '',
    stderr: 'error request_failed: the run decision body could not be read',
  },
  {
    name: 'refuses one byte over the decision cap without retrying',
    response: () => streamingAnswer('a'.repeat(262145)),
    code: 3,
    requests: 1,
    sleeps: [],
    stdout: '',
    stderr: 'error request_failed: the run decision body exceeded 262144 bytes',
  },
  {
    name: 'reads a null body as empty, which the JSON refusal then rejects',
    response: () => new Response(null),
    code: 3,
    requests: 1,
    sleeps: [],
    stdout: '',
    stderr: 'error request_failed: the run decision response was not valid: the body was not JSON',
  },
  {
    name: 'parses a complete decision body and reports its verdict',
    response: () => streamingAnswer(JSON.stringify(evaluated({ decision: 'PASS' }))),
    code: 0,
    requests: 1,
    sleeps: [],
    stdout: `gate: PASS (run ${runId})\n`,
    stderr: '',
  },
];

describe('gate bounded decision reader', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
  });

  it.each(readerCases)('$name', async (row) => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() => row.response());
    const out = capture();

    const { code, clock } = await invoke(
      gateArgv(['--run', runId, '--wait', '1s']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(row.code);
    expect(stub.calls).toHaveLength(row.requests);
    expect(clock.sleeps).toEqual(row.sleeps);
    expect(out.stdout.join('')).toBe(row.stdout);
    if (row.stderr === '') {
      expect(out.stderr.join('')).toBe('');
    } else {
      expect(out.stderr.join('')).toContain(row.stderr);
    }
    expect(out.stderr.join('')).not.toContain('private socket failure detail');
  });
});
