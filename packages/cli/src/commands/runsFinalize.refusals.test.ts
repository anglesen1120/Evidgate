// `evidgate runs finalize` refusals: the 403/422/409 surface (code and `request_id` verbatim,
// exactly one request), the `spool_missing` / `unsent_events` / `spool_corrupt` refusals, the
// live `writer.lock` refusal, and the flag and config validation that runs before any token
// work (contracts/cli.md §5, §9).
import { existsSync } from 'node:fs';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { writerLockFileName } from '../spoolLock.js';

import {
  runId,
  producerId,
  capture,
  stubFetch,
  invoke,
  accepted,
  refusal,
  stubDevFetch,
  apiCalls,
  finalizeArgv,
  finalizeWith,
  writerLockText,
  snapshotTree,
  useFinalizeSpec,
} from './runsFinalize.testing.js';

describe('runs finalize', () => {
  const { tempDir, writeSpool, devEnv, ingestEnv } = useFinalizeSpec();

  // AC6: each refused room exits 3 with the server `error.code` and `request_id`
  // verbatim, and the command makes exactly one request — a 403/422/409 is never retried.
  it('binding_errors_surface', async () => {
    const cases = [
      { status: 403, code: 'producer_slot_taken' },
      { status: 422, code: 'unknown_producer' },
      { status: 422, code: 'manifest_invalid' },
      { status: 409, code: 'idempotency_conflict' },
    ] as const;
    for (const { status, code } of cases) {
      const { spoolDir, producerDir } = await writeSpool({ spooled: 1, acked: 1 });
      const env = await devEnv(spoolDir);
      const stub = stubDevFetch(() => refusal(status, code));
      const out = capture();

      await expect(invoke(finalizeWith(), env, out, stub.fetchImpl)).resolves.toBe(3);

      expect(out.stdout.join('')).toBe('');
      const text = out.stderr.join('');
      expect(text).toContain(`evidgate: error ${code}: `);
      expect(text).toContain('evidgate: request_id: req-3');
      // Exactly one request: a permanent refusal is never retried.
      expect(apiCalls(stub)).toHaveLength(1);
      // A refused finalize keeps every byte: the slot was not declared.
      expect(existsSync(producerDir)).toBe(true);
    }
  });

  // A spool that still holds unacked events is refused, naming the unsent range and
  // making no request at all — a finalize never declares a sequence the server lacks.
  it('unsent_events_refuse', async () => {
    const { spoolDir, producerDir } = await writeSpool({ spooled: 3, acked: 1 });
    const env = await devEnv(spoolDir);
    const stub = stubFetch(() => accepted());
    const out = capture();

    await expect(invoke(finalizeWith(), env, out, stub.fetchImpl)).resolves.toBe(3);

    const text = out.stderr.join('');
    expect(text).toContain('evidgate: error unsent_events: ');
    expect(text).toContain(`run ${runId}`);
    expect(text).toContain('2..3');
    expect(text).toContain('evidgate: hint: ');
    expect(out.stdout.join('')).toBe('');
    expect(stub.calls).toHaveLength(0);
    expect(existsSync(producerDir)).toBe(true);
  });

  // No producer directory at all is `spool_missing`; finalize never creates one.
  it('missing_spool_refuses', async () => {
    const spoolDir = await tempDir();
    const env = await devEnv(spoolDir);
    const stub = stubFetch(() => accepted());
    const out = capture();

    await expect(invoke(finalizeWith(), env, out, stub.fetchImpl)).resolves.toBe(3);

    expect(out.stderr.join('')).toContain('evidgate: error spool_missing: ');
    expect(out.stdout.join('')).toBe('');
    expect(stub.calls).toHaveLength(0);
    expect(existsSync(join(spoolDir, runId, producerId))).toBe(false);
  });

  // Finalize is the destructive one, so a reporter that still holds the
  // producer's `writer.lock` refuses the command before the cursor is read — no request,
  // no purge, and the producer directory is byte-identical afterwards.
  it('refuses to finalize while the reporter still writes (D-S56-Q8)', async () => {
    const { spoolDir, producerDir } = await writeSpool({ spooled: 2, acked: 2 });
    const shardsText = `${JSON.stringify({ producer_id: producerId, sequence_next: 3 })}\n`;
    await writeFile(join(producerDir, 'shards.json'), shardsText);
    const artifactId = '5e0f6a3c-8b7d-4c2e-9f10-2a3b4c5d6e7f';
    await mkdir(join(producerDir, 'uploads'), { recursive: true, mode: 0o700 });
    await writeFile(join(producerDir, 'uploads', `${artifactId}.json`), '{"artifact_id":"x"}\n');
    // A live reporter: this test process's pid is alive for certain.
    await writeFile(join(producerDir, writerLockFileName), writerLockText(process.pid), {
      mode: 0o600,
    });
    const before = await snapshotTree(producerDir);
    const stub = stubFetch(() => accepted());
    const out = capture();

    const code = await invoke(finalizeWith(), await devEnv(spoolDir), out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toContain('spool_locked');
    expect(out.stderr.join('')).toContain(String(process.pid));
    expect(out.stderr.join('')).toContain('the reporter is still writing this producer');
    // Nothing was declared and nothing was purged.
    expect(stub.calls).toHaveLength(0);
    expect(existsSync(producerDir)).toBe(true);
    expect(await snapshotTree(producerDir)).toEqual(before);
  });

  // §5: every flag is validated before any token work, so a bad flag makes no request at
  // all — not even the token exchange (the `runs create` rule).
  it('flags_validated_before_token', async () => {
    const { spoolDir } = await writeSpool({ spooled: 1, acked: 1 });
    const env = await ingestEnv(spoolDir);
    const cases: ReadonlyArray<{ readonly argv: string[]; readonly message: string }> = [
      { argv: finalizeArgv(['--producer', producerId]), message: 'missing required flag --run' },
      {
        argv: finalizeArgv(['--run', 'not-a-uuid', '--producer', producerId]),
        message: '--run must be a UUID',
      },
      {
        argv: finalizeArgv(['--run', runId, '--producer', 'shard/1']),
        message: '--producer must be',
      },
      {
        argv: finalizeArgv(['--run', runId, '--producer', producerId, '--shard', '0']),
        message: '--shard must be an integer from 1 to 256',
      },
      {
        argv: finalizeArgv(['--run', runId, '--producer', producerId, '--shard', '257']),
        message: '--shard must be an integer from 1 to 256',
      },
      {
        argv: finalizeArgv(['--run', runId, '--producer', producerId, '--shard', '1.5']),
        message: '--shard must be an integer from 1 to 256',
      },
      {
        argv: finalizeArgv([
          '--run',
          runId,
          '--producer',
          producerId,
          '--shard',
          '1',
          '--manifest-hash',
          `sha256:${'A'.repeat(64)}`,
        ]),
        message: '--manifest-hash must be sha256:<64 lowercase hex>',
      },
      {
        argv: finalizeArgv([
          '--run',
          runId,
          '--producer',
          producerId,
          '--producer',
          'shard-2',
          '--shard',
          '1',
        ]),
        message: '--producer may be given only once',
      },
      {
        argv: finalizeWith(['--keep-spool', '--keep-spool']),
        message: '--keep-spool may be given only once',
      },
    ];
    for (const { argv, message } of cases) {
      const stub = stubFetch(() => accepted());
      const out = capture();
      await expect(invoke(argv, env, out, stub.fetchImpl)).resolves.toBe(3);
      expect(out.stderr.join('')).toContain('evidgate: error usage: ');
      expect(out.stderr.join('')).toContain(message);
      expect(out.stdout.join('')).toBe('');
      // No token exchange, no finalize: an ingest source would have needed the network.
      expect(stub.calls).toHaveLength(0);
    }
  });

  // The spool directory is required configuration; a missing value costs no request.
  it('missing_spool_dir_is_config_error', async () => {
    const { spoolDir } = await writeSpool({ spooled: 1, acked: 1 });
    const env = await devEnv(spoolDir);
    const withoutSpool: Record<string, string> = { ...env };
    delete withoutSpool['EVIDGATE_SPOOL_DIR'];
    const stub = stubFetch(() => accepted());
    const out = capture();

    await expect(invoke(finalizeWith(), withoutSpool, out, stub.fetchImpl)).resolves.toBe(3);

    expect(out.stderr.join('')).toContain('evidgate: error missing_config: ');
    expect(out.stderr.join('')).toContain('EVIDGATE_SPOOL_DIR');
    expect(stub.calls).toHaveLength(0);
  });

  // The spool surface is lazy and forced here, so a malformed cap is this command's
  // `invalid_config` and costs no request (it never reached `runs create` or `auth login`).
  it('malformed_spool_cap_is_config_error', async () => {
    const { spoolDir } = await writeSpool({ spooled: 1, acked: 1 });
    const env = await devEnv(spoolDir, { EVIDGATE_SPOOL_MAX_BYTES: '1MiB' });
    const stub = stubFetch(() => accepted());
    const out = capture();

    await expect(invoke(finalizeWith(), env, out, stub.fetchImpl)).resolves.toBe(3);

    const text = out.stderr.join('');
    expect(text).toContain('evidgate: error invalid_config: ');
    expect(text).toContain('EVIDGATE_SPOOL_MAX_BYTES');
    expect(text).not.toContain('1MiB');
    expect(stub.calls).toHaveLength(0);
  });

  // `cursor.json` is the only source of the declared values, so a count larger than
  // the sequence it claims to cover is corruption — no request, exit 3, spool kept.
  it('corrupt_cursor_count_refuses', async () => {
    const { spoolDir, producerDir } = await writeSpool({ spooled: 2, acked: 2 });
    await writeFile(
      join(producerDir, 'cursor.json'),
      `${JSON.stringify({
        producer_id: producerId,
        next_unacked_sequence: 3,
        last_acked_sequence: 2,
        acked_count: 9,
      })}\n`,
      { mode: 0o600 },
    );
    const env = await devEnv(spoolDir);
    const stub = stubFetch(() => accepted());
    const out = capture();

    await expect(invoke(finalizeWith(), env, out, stub.fetchImpl)).resolves.toBe(3);

    const text = out.stderr.join('');
    expect(text).toContain('evidgate: error spool_corrupt: ');
    expect(text).toContain('acked_count');
    expect(text).toContain('last_acked_sequence');
    expect(out.stdout.join('')).toBe('');
    expect(stub.calls).toHaveLength(0);
    expect(existsSync(producerDir)).toBe(true);
  });
});
