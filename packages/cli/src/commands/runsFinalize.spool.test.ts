// `evidgate runs finalize` spool lifecycle: the purge after a 202, `--keep-spool`, a purge
// that fails after the run IS finalized, and the offline failures (a 401, a transport error)
// that keep the spool untouched (§8 retention, §5).
import { existsSync } from 'node:fs';
import { chmod, mkdir, readdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { readCachedToken, writeCachedToken } from '../auth/tokenCache.js';
import { resolveConfig } from '../config.js';
import { writerLockFileName } from '../spoolLock.js';

import {
  runId,
  producerId,
  shard,
  capture,
  stubFetch,
  invoke,
  jsonResponse,
  accepted,
  stubDevFetch,
  apiCalls,
  sentBody,
  finalizeWith,
  exitedPid,
  writerLockText,
  useFinalizeSpec,
} from './runsFinalize.testing.js';

describe('runs finalize', () => {
  const { writeSpool, devEnv, ingestEnv } = useFinalizeSpec();

  // §8 retention: a 202 purges the producer directory whole.
  it('purges_after_success', async () => {
    const { spoolDir, producerDir } = await writeSpool({ spooled: 2, acked: 2 });
    expect(existsSync(producerDir)).toBe(true);
    const env = await devEnv(spoolDir);
    const stub = stubDevFetch(() => accepted());
    const out = capture();

    await expect(invoke(finalizeWith(), env, out, stub.fetchImpl)).resolves.toBe(0);

    expect(existsSync(producerDir)).toBe(false);
    // The run directory itself is left for the other producers of that run.
    expect(existsSync(join(spoolDir, runId))).toBe(true);
  });

  // §8 retention opt-out: `--keep-spool` keeps every chunk and the cursor.
  it('keep_spool_retains', async () => {
    const { spoolDir, producerDir } = await writeSpool({ spooled: 2, acked: 2 });
    const env = await devEnv(spoolDir);
    const stub = stubDevFetch(() => accepted());

    const first = capture();
    await expect(invoke(finalizeWith(['--keep-spool']), env, first, stub.fetchImpl)).resolves.toBe(
      0,
    );

    expect(existsSync(producerDir)).toBe(true);
    const files = await readdir(producerDir);
    expect(files).toContain('000001.jsonl');
    expect(files).toContain('cursor.json');
    // The lock file is released, so a second finalize can run (the server answers the
    // same no-op 202) and re-derives the same key.
    expect(files).not.toContain('spool.lock');

    const second = capture();
    await expect(invoke(finalizeWith(['--keep-spool']), env, second, stub.fetchImpl)).resolves.toBe(
      0,
    );
    const calls = apiCalls(stub);
    expect(calls).toHaveLength(2);
    expect(calls[1]?.request.headers.get('idempotency-key')).toBe(
      calls[0]?.request.headers.get('idempotency-key'),
    );
    // Identical cursor values, so the second request is byte-identical too.
    expect(calls[1]?.body.toString('utf8')).toBe(calls[0]?.body.toString('utf8') ?? '');
  });

  // A purge that fails after a 202 is a stderr warning and still exits 0: the run IS
  // finalized, and a leftover spool is only local disk.
  it('purge_failure_is_a_warning', async () => {
    const { spoolDir, producerDir } = await writeSpool({ spooled: 1, acked: 1 });
    const runDirectory = join(spoolDir, runId);
    const env = await devEnv(spoolDir);
    // Removing the producer entry needs write permission on the run directory. Taking it
    // away inside the stub (between the cursor read and the purge) makes `rm` fail with
    // EACCES on POSIX; win32 has no such mode and root bypasses it, so both skip.
    if (process.platform === 'win32' || process.getuid?.() === 0) return;
    const stub = stubDevFetch(async () => {
      await chmod(runDirectory, 0o500);
      return accepted();
    });
    const out = capture();

    try {
      await expect(invoke(finalizeWith(), env, out, stub.fetchImpl)).resolves.toBe(0);
      // The output is the success line; the warning goes to stderr only.
      expect(out.stdout.join('')).toContain('reconcile accepted (asynchronous)');
      expect(out.stderr.join('')).toContain('evidgate: warning: ');
      expect(out.stderr.join('')).toContain('could not be purged');
      expect(out.stderr.join('')).toContain('EACCES');
      expect(existsSync(producerDir)).toBe(true);
    } finally {
      await chmod(runDirectory, 0o700);
    }
  });

  it('finalizes and purges a producer dir that also holds shards.json, uploads/ and a dead writer.lock', async () => {
    const { spoolDir, producerDir } = await writeSpool({ spooled: 2, acked: 2 });
    await writeFile(
      join(producerDir, 'shards.json'),
      `${JSON.stringify({ producer_id: producerId, sequence_next: 3 })}\n`,
    );
    const artifactId = '5e0f6a3c-8b7d-4c2e-9f10-2a3b4c5d6e7f';
    await mkdir(join(producerDir, 'uploads'), { recursive: true, mode: 0o700 });
    await writeFile(join(producerDir, 'uploads', `${artifactId}.json`), '{"artifact_id":"x"}\n');
    // A lock left by a reporter that died is ignored (the reporter's next start owns the
    // takeover), so the purge still drains and removes the whole producer directory.
    await writeFile(join(producerDir, writerLockFileName), writerLockText(exitedPid()), {
      mode: 0o600,
    });
    const stub = stubDevFetch(() => accepted());
    const out = capture();

    const code = await invoke(finalizeWith(), await devEnv(spoolDir), out, stub.fetchImpl);

    expect(code).toBe(0);
    const calls = apiCalls(stub);
    expect(calls).toHaveLength(1);
    expect(sentBody(calls[0])).toMatchObject({
      producer_id: producerId,
      shard,
      last_sequence: 2,
      count: 2,
    });
    expect(out.stdout.join('')).toContain('reconcile accepted (asynchronous)');
    expect(existsSync(producerDir)).toBe(false);
  });

  it('keeps the spool when the reporter starts writing during the finalize request', async () => {
    const { spoolDir, producerDir } = await writeSpool({ spooled: 1, acked: 1 });
    // The reporter claims the producer between the cursor read and the purge.
    const stub = stubDevFetch(async () => {
      await writeFile(join(producerDir, writerLockFileName), writerLockText(process.pid), {
        mode: 0o600,
      });
      return accepted();
    });
    const out = capture();

    await expect(invoke(finalizeWith(), await devEnv(spoolDir), out, stub.fetchImpl)).resolves.toBe(
      0,
    );

    expect(out.stdout.join('')).toContain('reconcile accepted (asynchronous)');
    expect(out.stderr.join('')).toContain('could not be purged');
    expect(out.stderr.join('')).toContain('the reporter started writing again');
    // The run IS finalized, but the live reporter keeps its spool.
    expect(existsSync(join(producerDir, writerLockFileName))).toBe(true);
    expect(existsSync(join(producerDir, '000001.jsonl'))).toBe(true);
  });

  // AC6 / contracts/cli.md §7: a 401 purges the cached CI token; the server error surfaces.
  it('unauthorized_purges_cached_token', async () => {
    const { spoolDir } = await writeSpool({ spooled: 1, acked: 1 });
    const env = await ingestEnv(spoolDir);
    const config = resolveConfig({ env });
    const expiresAt = new Date(Date.now() + 30 * 60_000).toISOString();
    await writeCachedToken(config, { token: 'egt_cached_token', expiresAt });

    const stub = stubFetch(() =>
      jsonResponse(401, {
        error: {
          code: 'token_expired',
          message: 'the token expired',
          request_id: 'req-401',
          retryable: false,
        },
      }),
    );
    const out = capture();

    await expect(invoke(finalizeWith(), env, out, stub.fetchImpl)).resolves.toBe(3);

    expect(out.stderr.join('')).toContain('evidgate: error token_expired: ');
    expect(out.stderr.join('')).toContain('evidgate: request_id: req-401');
    // The cached token was reused (no exchange), so the finalize is the only call.
    expect(stub.calls).toHaveLength(1);
    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });

  // §5: a transport failure is exit 3 `transport` with a fixed message, and the spool is
  // kept (nothing was declared).
  it('transport_failure_keeps_spool', async () => {
    const { spoolDir, producerDir } = await writeSpool({ spooled: 1, acked: 1 });
    const env = await devEnv(spoolDir);
    // The exchange answers, so the failure is the finalize's own transport error.
    const stub = stubDevFetch(() => {
      throw new TypeError('network down');
    });
    const out = capture();

    await expect(invoke(finalizeWith(), env, out, stub.fetchImpl)).resolves.toBe(3);

    expect(out.stderr.join('')).toContain('evidgate: error transport: ');
    expect(out.stdout.join('')).toBe('');
    // A finalize is replay-safe (derived key, same body), so the transport failure is
    // retried up to the bounded attempt count before the command fails.
    expect(apiCalls(stub)).toHaveLength(3);
    expect(existsSync(producerDir)).toBe(true);
  });
});
