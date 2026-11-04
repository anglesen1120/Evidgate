// Reading `--from` input: `-` streams from the injected stdin across chunk boundaries, and an
// input naming another run or producer, or carrying a malformed line, spools nothing.
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';
import { listProducers } from '../spool.js';

import {
  acked,
  baseEnv,
  capture,
  cleanupSpecState,
  envelopeText,
  invoke,
  otherProducer,
  runId,
  sendArgv,
  stubFetch,
  tempDir,
} from './eventsSend.testing.js';

describe('events send', () => {
  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await cleanupSpecState();
  });

  it('reads --from - from the injected stdin, across chunk boundaries', async () => {
    const spoolDir = await tempDir();
    // CRLF line endings and a chunk boundary inside the first envelope.
    const bytes = Buffer.from(`${[envelopeText(1), envelopeText(2)].join('\r\n')}\r\n`, 'utf8');
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv(['--from', '-']), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      stdin: (async function* stream() {
        await Promise.resolve();
        yield bytes.subarray(0, 40);
        yield bytes.subarray(40);
      })(),
    });

    expect(code).toBe(0);
    expect(stub.calls).toHaveLength(1);
    expect(stub.calls[0]?.events).toHaveLength(2);
    expect(out.stdout.join('')).toContain('batches=1 accepted=2 duplicate=0');
  });

  it('refuses --from input of another run or producer, spooling nothing', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const otherRun = '11111111-2222-4333-8444-555555555555';
    const cases: ReadonlyArray<{
      readonly name: string;
      readonly argv: string[];
      readonly lines: string[];
    }> = [
      {
        name: 'run',
        argv: sendArgv(['--from', join(input, 'run.jsonl')]),
        lines: [envelopeText(1, { runId: otherRun })],
      },
      {
        name: 'producer',
        argv: sendArgv(['--producer', otherProducer, '--from', join(input, 'producer.jsonl')]),
        lines: [envelopeText(1)],
      },
    ];
    for (const testCase of cases) {
      const file = testCase.argv[testCase.argv.indexOf('--from') + 1] ?? '';
      await writeFile(file, `${testCase.lines.join('\n')}\n`);
      const stub = stubFetch((call) => acked(call));
      const out = capture();
      const code = await invoke(testCase.argv, baseEnv(spoolDir), out, {
        fetch: stub.fetchImpl,
      });
      expect(code).toBe(3);
      expect(stub.calls).toHaveLength(0);
      expect(out.stderr.join('')).toContain('evidgate: error usage:');
    }
    expect(await listProducers(spoolDir, runId)).toEqual([]);
  });

  it('refuses malformed --from input with the code that names the problem', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const cases: ReadonlyArray<{ readonly lines: string[]; readonly code: string }> = [
      { lines: ['not json'], code: 'envelope_invalid' },
      { lines: [envelopeText(1, { type: 'Bad Type' })], code: 'envelope_invalid' },
      { lines: ['[]'], code: 'envelope_invalid' },
      { lines: [envelopeText(2)], code: 'invalid_spool_input' },
      { lines: [envelopeText(1), envelopeText(1)], code: 'invalid_spool_input' },
      { lines: [envelopeText(1), ''], code: 'invalid_spool_input' },
    ];
    for (const [index, testCase] of cases.entries()) {
      const file = join(input, `case-${index}.jsonl`);
      await writeFile(file, `${testCase.lines.join('\n')}\n`);
      const stub = stubFetch((call) => acked(call));
      const out = capture();
      const code = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), out, {
        fetch: stub.fetchImpl,
      });
      expect(code).toBe(3);
      expect(stub.calls).toHaveLength(0);
      expect(out.stderr.join('')).toContain(`evidgate: error ${testCase.code}:`);
    }
    // Nothing was spooled by any of the refused files, and no directory was created.
    expect(await listProducers(spoolDir, runId)).toEqual([]);
  });
});
