// The server's refusal surface: an event too large for one batch body names its event_id, and
// a 403/422 is surfaced verbatim with its request id — never retried, never acked.
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import {
  acked,
  baseEnv,
  capture,
  cleanupSpecState,
  envelopeText,
  errorResponse,
  eventIdFor,
  invoke,
  openSpool,
  runId,
  sendArgv,
  stubFetch,
  tempDir,
  virtualClock,
} from './eventsSend.testing.js';

describe('events send', () => {
  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await cleanupSpecState();
  });

  it('fails an event too large for one batch body, naming its event_id (AC4)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const file = join(input, 'events.jsonl');
    await writeFile(file, `${envelopeText(1, { pad: 1_100_000 })}\n`);
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
    });

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error event_too_large:');
    expect(stderr).toContain(eventIdFor(1));
    expect(stderr).toContain('sequence 1');
    const spool = await openSpool(spoolDir);
    expect((await spool.cursor()).nextUnackedSequence).toBe(1);
  });

  for (const refusal of [
    { status: 403, code: 'producer_slot_taken' },
    { status: 422, code: 'unknown_producer' },
  ]) {
    it(`surfaces ${refusal.status} ${refusal.code} verbatim with its id (AC6)`, async () => {
      const input = await tempDir();
      const spoolDir = await tempDir();
      const file = join(input, 'events.jsonl');
      await writeFile(file, `${[envelopeText(1), envelopeText(2)].join('\n')}\n`);
      const stub = stubFetch(() =>
        errorResponse(refusal.status, refusal.code, `req-${refusal.status}`),
      );
      const clock = virtualClock();
      const out = capture();

      const code = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), out, {
        fetch: stub.fetchImpl,
        now: clock.now,
        sleep: clock.sleep,
        random: () => 0.5,
      });

      expect(code).toBe(3);
      // 403/422 are permanent: never retried, so exactly one request and no wait.
      expect(stub.calls).toHaveLength(1);
      expect(clock.sleeps).toEqual([]);
      const stderr = out.stderr.join('');
      expect(stderr).toContain(`evidgate: error ${refusal.code}:`);
      expect(stderr).toContain(`evidgate: request_id: req-${refusal.status}`);
      expect(stderr).toContain(`run ${runId}`);
      expect(stderr).toContain('unsent sequences 1..2');
      expect((await openSpool(spoolDir).then((spool) => spool.cursor())).nextUnackedSequence).toBe(
        1,
      );
    });
  }
});
