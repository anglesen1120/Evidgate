// The command's main path: `--from` lines are spooled per producer, each producer's events go
// out in one batch, and the run reports per producer — as text, as one JSON document, or as
// "nothing to send" when every event is already acked.
import { readFile, stat, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';
import { listProducers } from '../spool.js';

import { devToken, mintedToken } from './devExchange.testing.js';
import {
  acked,
  baseEnv,
  capture,
  cleanupSpecState,
  envelopeText,
  invoke,
  listFiles,
  openSpool,
  otherProducer,
  producer,
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

  it('spools --from lines by producer, delivers them and reports per producer', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const file = join(input, 'events.jsonl');
    await writeFile(
      file,
      [envelopeText(1), envelopeText(1, { producerId: otherProducer }), envelopeText(2)].join(
        '\n',
      ) + '\n',
    );
    const clock = virtualClock();
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv(['--from', file]), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
      now: clock.now,
      sleep: clock.sleep,
      random: () => 0.5,
    });

    expect(code).toBe(0);
    expect(await listProducers(spoolDir, runId)).toEqual([producer, otherProducer]);
    // One exchange for the dev token, then one batch per producer.
    expect(stub.exchanges).toHaveLength(1);
    expect(stub.calls).toHaveLength(2);
    // Every batch carries the minted bearer: the raw dev token only travels in the exchange
    // body, so a regression that sent it as the bearer fails here.
    expect(stub.calls.map((call) => call.request.headers.get('authorization'))).toStrictEqual([
      `Bearer ${mintedToken}`,
      `Bearer ${mintedToken}`,
    ]);
    expect(JSON.parse(stub.exchanges[0]?.body.toString('utf8') ?? '')).toStrictEqual({
      ingest_token: devToken,
    });
    // One line per producer, in the sorted order `listProducers` returns.
    expect(out.stdout.join('')).toBe(
      `evidgate: sent run=${runId} producer=${producer} batches=1 accepted=2 duplicate=0\n` +
        `evidgate: sent run=${runId} producer=${otherProducer} batches=1 accepted=1 ` +
        'duplicate=0\n',
    );
    expect(out.stderr.join('')).toBe('');

    const first = await openSpool(spoolDir);
    expect(await first.cursor()).toMatchObject({
      nextUnackedSequence: 3,
      lastAckedSequence: 2,
      ackedCount: 2,
    });
    const second = await openSpool(spoolDir, otherProducer);
    expect((await second.cursor()).nextUnackedSequence).toBe(2);

    // S3 analogue: the token is in no spool file, and the CLI-owned files are locked down.
    const files = await listFiles(join(spoolDir, runId));
    expect(files.length).toBeGreaterThan(0);
    for (const path of files) {
      expect(await readFile(path, 'utf8')).not.toContain(devToken);
      expect(await readFile(path, 'utf8')).not.toContain(mintedToken);
    }
    expect(out.stdout.join('')).not.toContain(devToken);
    expect(out.stdout.join('')).not.toContain(mintedToken);
    expect((await stat(join(spoolDir, runId, producer, 'cursor.json'))).mode & 0o777).toBe(0o600);
    expect((await stat(join(spoolDir, runId, producer))).mode & 0o777).toBe(0o700);
  });

  it('prints one JSON document with --json, including the nothing-to-send case', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const file = join(input, 'events.jsonl');
    await writeFile(file, `${[envelopeText(1), envelopeText(2)].join('\n')}\n`);
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv(['--from', file, '--json']), baseEnv(spoolDir), out, {
      fetch: stub.fetchImpl,
    });

    expect(code).toBe(0);
    expect(JSON.parse(out.stdout.join(''))).toEqual({
      run_id: runId,
      producers: [{ producer_id: producer, batches: 1, accepted: 2, duplicates: 0 }],
      batches: 1,
      accepted: 2,
      duplicates: 0,
    });

    // Everything is acked now: a second run sends nothing and makes no request.
    const idleStub = stubFetch((call) => acked(call));
    const idleOut = capture();
    const idleCode = await invoke(sendArgv(['--json']), baseEnv(spoolDir), idleOut, {
      fetch: idleStub.fetchImpl,
    });
    expect(idleCode).toBe(0);
    expect(idleStub.calls).toHaveLength(0);
    expect(JSON.parse(idleOut.stdout.join(''))).toEqual({
      run_id: runId,
      producers: [],
      batches: 0,
      accepted: 0,
      duplicates: 0,
    });
  });

  it('says nothing to send for a run with no spool, without a request', async () => {
    const spoolDir = await tempDir();
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(sendArgv([]), baseEnv(spoolDir), out, { fetch: stub.fetchImpl });

    expect(code).toBe(0);
    expect(stub.calls).toHaveLength(0);
    expect(out.stdout.join('')).toBe(`evidgate: nothing to send run=${runId}\n`);
  });
});
