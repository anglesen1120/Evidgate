// 04.2.3-T2.1 / AC6: `evidgate runs finalize` on the binary — the values it reads from the
// ack cursor, the `Idempotency-Key` derivation, the 202 reconcile-accepted output, the
// 403 `producer_slot_taken` / 422 `unknown_producer` / 422 `manifest_invalid` surface
// (exactly one request, code + `request_id` verbatim, T9), the refusals (`spool_missing`,
// `unsent_events`), the purge / `--keep-spool` pair, the flag validation that runs before
// any token work, the 401-only cache purge and the rule that the token lives only in the
// cache — never in the spool or a stream
// (04.2.3 §2, §5, §8, §9, §10; contracts/cli.md §5/§7/§11).
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync } from 'node:fs';
import { chmod, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { readCachedToken, writeCachedToken } from '../auth/tokenCache.js';
import { resolveConfig } from '../config.js';
import { main } from '../main.js';
import { resetSecrets, type Io } from '../redact.js';
import { defaultSpoolMaxBytes, ProducerSpool } from '../spool.js';
import { writerLockFileName } from '../spoolLock.js';

import { devExchangeAnswer, devToken, isDevExchange, mintedToken } from './devExchange.testing.js';
import { deriveFinalizeIdempotencyKey } from './runsFinalize.js';

/** The project slug the command scopes the finalize to. */
const project = 'proj-1';
/** The run the slot belongs to (openapi `RunId`, `format: uuid`). */
const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
/** The manifest's deterministic producer id for shard 1 (T54, `shard-<index>`). */
const producerId = 'shard-1';
/** The 1-based manifest shard index of that producer. */
const shard = 1;
/** A valid `manifest_hash` (`sha256:` + 64 lowercase hex); assembled, no digest literal. */
const manifestHash = `sha256:${'a'.repeat(64)}`;
/** The namespace of the documented derived-key pre-image. */
const keyVersion = 'evidgate.finalize-key.v1';

interface Captured {
  readonly io: Io;
  readonly stdout: string[];
  readonly stderr: string[];
}

function capture(): Captured {
  const stdout: string[] = [];
  const stderr: string[] = [];
  return {
    io: { stdout: (text) => stdout.push(text), stderr: (text) => stderr.push(text) },
    stdout,
    stderr,
  };
}

interface StubCall {
  readonly request: Request;
  readonly body: Buffer;
}

/** A `fetch` stub that records every request (URL, headers, raw body) and answers from `handler`. */
function stubFetch(handler: (request: Request) => Response | Promise<Response>): {
  readonly fetchImpl: typeof fetch;
  readonly calls: StubCall[];
} {
  const calls: StubCall[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    calls.push({ request, body: Buffer.from(await request.clone().arrayBuffer()) });
    return handler(request);
  };
  return { fetchImpl, calls };
}

/**
 * `main()` with the injected `fetch` stub — the only transport the specs use — and a
 * retry backoff that neither waits nor jitters, so a retried request stays instant.
 */
function invoke(
  argv: ReadonlyArray<string>,
  env: Readonly<Record<string, string | undefined>>,
  out: Captured,
  fetchImpl: typeof fetch,
): Promise<number> {
  return main(argv, env, out.io, {
    fetch: fetchImpl,
    sleep: () => Promise.resolve(),
    random: () => 0,
  });
}

function jsonResponse(
  status: number,
  body: unknown,
  headers: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json', ...headers },
  });
}

/** A 202 with no body (openapi `finalizeRun`: "Finalize accepted; reconcile async"). */
function accepted(headers: Record<string, string> = {}): Response {
  return new Response(null, { status: 202, headers });
}

/** The 00 §9.2 error envelope the server answers a refused finalize with. */
function refusal(status: number, code: string, requestId = 'req-3'): Response {
  return jsonResponse(status, {
    error: { code, message: 'the finalize was refused', request_id: requestId, retryable: false },
  });
}

/** The `project_id` the exchange echoes back (openapi `format: uuid`). */
const exchangeProjectId = '5a0b1c2d-3e4f-4a5b-8c6d-7e8f9a0b1c2d';

/**
 * A stub whose handler answers the command's own request, never the token exchange: the dev
 * token is exchanged like an ingest credential, so the bearer the command sends is the token
 * the exchange hands out. The minted token expires 30 minutes out, inside the cache's 60 + 5
 * minute freshness window, and it is a different string from the credential sent in, so an
 * exchange that was skipped shows up as a `Bearer ${devToken}` mismatch.
 */
function stubDevFetch(handler: (request: Request) => Response | Promise<Response>): {
  readonly fetchImpl: typeof fetch;
  readonly calls: StubCall[];
} {
  return stubFetch(
    (request) =>
      devExchangeAnswer(request, {
        projectId: exchangeProjectId,
        expiresAt: new Date(Date.now() + 30 * 60_000).toISOString(),
      }) ?? handler(request),
  );
}

/**
 * The command's own requests: everything but the `/v1/ci/token` exchange, so a count or an
 * index means the same thing it did before the dev source gained a token exchange.
 */
function apiCalls(stub: { readonly calls: StubCall[] }): StubCall[] {
  return stub.calls.filter((call) => !isDevExchange(call.request));
}

/** The JSON body the stub recorded for `call`. */
function sentBody(call: StubCall | undefined): Record<string, unknown> {
  return JSON.parse(call?.body.toString('utf8') ?? '{}') as Record<string, unknown>;
}

/** A bare `runs finalize` argv; every case supplies the flags it needs. */
function finalizeArgv(flags: ReadonlyArray<string>): string[] {
  return ['runs', 'finalize', ...flags];
}

/** The common `runs finalize` invocation: every required flag, plus `extra`. */
function finalizeWith(extra: ReadonlyArray<string> = []): string[] {
  return finalizeArgv([
    '--run',
    runId,
    '--producer',
    producerId,
    '--shard',
    String(shard),
    '--manifest-hash',
    manifestHash,
    ...extra,
  ]);
}

/** A valid envelope line: the reader only requires `sequence` and an `event_id`. */
function envelope(sequence: number): string {
  return JSON.stringify({ sequence, event_id: `evt-${sequence}`, type: 'test.finished' });
}

/** A pid that is certainly dead: a child process spawned and already waited for. */
function exitedPid(): number {
  const child = spawnSync(process.execPath, ['-e', ''], { stdio: 'ignore' });
  if (child.pid === undefined) throw new Error('could not start the short-lived child');
  return child.pid;
}

/** The whole `{ pid, started_at }` record the reporter writes into `writer.lock`. */
function writerLockText(pid: number): string {
  return `${JSON.stringify({ pid, started_at: '2026-01-01T00:00:00.000Z' })}\n`;
}

/** Every file under `directory` (recursive) with its bytes, for a byte-identity assertion. */
async function snapshotTree(directory: string): Promise<Array<readonly [string, string]>> {
  const entries = await readdir(directory, { withFileTypes: true, recursive: true });
  const snapshot: Array<readonly [string, string]> = [];
  for (const entry of entries.filter((candidate) => candidate.isFile())) {
    const path = join(entry.parentPath, entry.name);
    snapshot.push([relative(directory, path), await readFile(path, 'utf8')]);
  }
  return snapshot.sort((a, b) => a[0].localeCompare(b[0]));
}

describe('runs finalize', () => {
  const dirs: string[] = [];

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  async function tempDir(): Promise<string> {
    const dir = await mkdtemp(join(tmpdir(), 'evidgate-cli-'));
    dirs.push(dir);
    return dir;
  }

  /**
   * Writes a producer spool with `spooled` envelopes and acks the first `acked` of them,
   * so the cursor holds `{last_acked_sequence: acked, acked_count: acked}` (AC6).
   */
  async function writeSpool(o: { spooled: number; acked: number }): Promise<{
    readonly spoolDir: string;
    readonly producerDir: string;
  }> {
    const spoolDir = await tempDir();
    const spool = await ProducerSpool.open({
      spoolDir,
      runId,
      producerId,
      maxBytes: defaultSpoolMaxBytes,
      createIfMissing: true,
    });
    if (o.spooled > 0) {
      const lines = Array.from({ length: o.spooled }, (_, index) => envelope(index + 1));
      await spool.append(lines);
    }
    if (o.acked > 0) {
      await spool.ack(o.acked, o.acked);
    }
    return { spoolDir, producerDir: join(spoolDir, runId, producerId) };
  }

  /**
   * The dev path: the dev token is exchanged at `/v1/ci/token` like an ingest credential,
   * the returned producer token is the bearer, and it is cached.
   */
  async function devEnv(spoolDir: string, extra: Record<string, string> = {}) {
    const cacheDir = await tempDir();
    return {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: project,
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: devToken,
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_SPOOL_DIR: spoolDir,
      ...extra,
    };
  }

  /** An ingest source: `getToken()` would exchange at `/v1/ci/token` when the cache is cold. */
  async function ingestEnv(spoolDir: string, extra: Record<string, string> = {}) {
    const cacheDir = await tempDir();
    return {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: project,
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_INGEST_TOKEN: 'egt_ingest_secret_value',
      EVIDGATE_SPOOL_DIR: spoolDir,
      ...extra,
    };
  }

  // AC6: a 202 prints the reconcile-accepted state, and the request carries exactly the
  // five openapi members — `last_sequence`/`count` come from the cursor, not from flags.
  it('finalizes_with_cursor_values', async () => {
    const { spoolDir } = await writeSpool({ spooled: 3, acked: 3 });
    const env = await devEnv(spoolDir);
    const stub = stubDevFetch(() => accepted());
    const out = capture();

    await expect(invoke(finalizeWith(), env, out, stub.fetchImpl)).resolves.toBe(0);

    expect(out.stdout.join('')).toBe(
      `evidgate: finalized run=${runId} producer=${producerId} shard=1 ` +
        'last_sequence=3 count=3; reconcile accepted (asynchronous)\n',
    );
    expect(out.stderr.join('')).toBe('');
    const calls = apiCalls(stub);
    expect(calls).toHaveLength(1);
    const call = calls[0];
    expect(call?.request.method).toBe('POST');
    // `createApiClient` puts the SDK's `/v1` server prefix on the base URL.
    expect(new URL(call?.request.url ?? '').pathname).toBe(`/v1/runs/${runId}/finalize`);
    expect(call?.request.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
    const key = call?.request.headers.get('idempotency-key') ?? '';
    expect(key).toMatch(/^[0-9a-f]{64}$/);
    expect(key).toBe(
      deriveFinalizeIdempotencyKey({
        runId,
        producerId,
        shard,
        lastSequence: 3,
        count: 3,
        manifestHash,
      }),
    );
    // `FinalizeRunData.body` has `additionalProperties: false`: only these five members.
    expect(sentBody(call)).toEqual({
      producer_id: producerId,
      shard: 1,
      last_sequence: 3,
      count: 3,
      manifest_hash: manifestHash,
    });
    expect(Object.keys(sentBody(call)).sort()).toEqual([
      'count',
      'last_sequence',
      'manifest_hash',
      'producer_id',
      'shard',
    ]);
  });

  // AC6: `--json` prints one line with the documented key order and the accepted state.
  it('json_prints_reconcile_accepted', async () => {
    const { spoolDir } = await writeSpool({ spooled: 2, acked: 2 });
    const env = await devEnv(spoolDir);
    const stub = stubDevFetch(() => accepted());
    const out = capture();

    await expect(invoke(finalizeWith(['--json']), env, out, stub.fetchImpl)).resolves.toBe(0);

    expect(out.stdout.join('')).toBe(
      `${JSON.stringify({
        run_id: runId,
        producer_id: producerId,
        shard: 1,
        last_sequence: 2,
        count: 2,
        manifest_hash: manifestHash,
        state: 'reconcile_accepted',
      })}\n`,
    );
    expect(apiCalls(stub)).toHaveLength(1);
  });

  // AC6: the key is derived from the request values alone, so two separate invocations over
  // the same acked state send the same key (a retry after a lost 202 is the same no-op),
  // while a different cursor state — or a different manifest hash — never reuses it.
  it('idempotency_key_stable_across_invocations', async () => {
    const keysOf = async (acked: number, hash = manifestHash): Promise<string> => {
      const { spoolDir } = await writeSpool({ spooled: acked, acked });
      const env = await devEnv(spoolDir);
      const stub = stubDevFetch(() => accepted());
      const out = capture();
      const argv = finalizeArgv([
        '--run',
        runId,
        '--producer',
        producerId,
        '--shard',
        String(shard),
        '--manifest-hash',
        hash,
      ]);
      await expect(invoke(argv, env, out, stub.fetchImpl)).resolves.toBe(0);
      const calls = apiCalls(stub);
      expect(calls).toHaveLength(1);
      return calls[0]?.request.headers.get('idempotency-key') ?? '';
    };

    const first = await keysOf(2);
    const second = await keysOf(2);
    expect(first).toMatch(/^[0-9a-f]{64}$/);
    expect(second).toBe(first);
    expect(await keysOf(3)).not.toBe(first);
    expect(await keysOf(2, `sha256:${'c'.repeat(64)}`)).not.toBe(first);
  });

  // AC6 / T9: each refused room exits 3 with the server `error.code` and `request_id`
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

  // D7: a spool that still holds unacked events is refused, naming the unsent range and
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

  // D7: no producer directory at all is `spool_missing`; finalize never creates one.
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

  // D-S56-Q8: finalize is the destructive one, so a reporter that still holds the
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

  // D5: the spool directory is required configuration; a missing value costs no request.
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

  // CR8: the spool surface is lazy and forced here, so a malformed cap is this command's
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

  // SC9: `cursor.json` is the only source of the declared values, so a count larger than
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

  // T78 / §8: the producer token reaches the cache and nothing else — not the spool, not a
  // stream — and the dev credential it was exchanged from never leaves the environment.
  it('exchanged_token_lives_only_in_the_cache', async () => {
    const { spoolDir, producerDir } = await writeSpool({ spooled: 2, acked: 2 });
    const env = await devEnv(spoolDir, { EVIDGATE_SPOOL_MAX_BYTES: '1048576' });
    const producerToken = 'egt_producer_token_for_finalize';
    const stub = stubFetch(
      (request) =>
        devExchangeAnswer(request, {
          token: producerToken,
          projectId: exchangeProjectId,
          expiresAt: new Date(Date.now() + 30 * 60_000).toISOString(),
        }) ?? accepted(),
    );
    const out = capture();

    await expect(invoke(finalizeWith(['--keep-spool']), env, out, stub.fetchImpl)).resolves.toBe(0);

    // The exchanged token, not the dev credential, is the bearer.
    expect(apiCalls(stub)[0]?.request.headers.get('authorization')).toBe(`Bearer ${producerToken}`);
    const everything = `${out.stdout.join('')}${out.stderr.join('')}`;
    expect(everything).not.toContain(devToken);
    expect(everything).not.toContain(producerToken);
    for (const name of await readdir(producerDir)) {
      const content = await readFile(join(producerDir, name), 'utf8');
      expect(content.includes(devToken), `dev token in ${name}`).toBe(false);
      expect(content.includes(producerToken), `producer token in ${name}`).toBe(false);
    }
    // The dev path now exchanges, so the cache holds exactly the exchanged token (and
    // never the dev credential it was exchanged from).
    const cached = await readCachedToken(resolveConfig({ env }));
    expect(cached?.token).toBe(producerToken);
    const cacheFiles = await readdir(String(env.EVIDGATE_CACHE_DIR));
    expect(cacheFiles).toHaveLength(1);
    const cacheText = await readFile(
      join(String(env.EVIDGATE_CACHE_DIR), cacheFiles[0] ?? ''),
      'utf8',
    );
    expect(cacheText).not.toContain(devToken);
  });
});

describe('deriveFinalizeIdempotencyKey', () => {
  const input = { runId, producerId, shard, lastSequence: 7, count: 7, manifestHash };

  // AC6: 64 lowercase hex, stable across calls, and inside the 128-char route cap.
  it('derives a stable sha256 hex key', () => {
    const key = deriveFinalizeIdempotencyKey(input);
    expect(key).toMatch(/^[0-9a-f]{64}$/);
    expect(key.length).toBeLessThanOrEqual(128);
    expect(deriveFinalizeIdempotencyKey(input)).toBe(key);
  });

  // Every value the command can send in the body feeds the key, so a different body never
  // reuses it (a changed cursor included).
  it('moves with every request value', () => {
    const key = deriveFinalizeIdempotencyKey(input);
    const variants = [
      deriveFinalizeIdempotencyKey({ ...input, runId: '1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f' }),
      deriveFinalizeIdempotencyKey({ ...input, producerId: 'shard-2' }),
      deriveFinalizeIdempotencyKey({ ...input, shard: 2 }),
      deriveFinalizeIdempotencyKey({ ...input, lastSequence: 8, count: 8 }),
      deriveFinalizeIdempotencyKey({ ...input, count: 6 }),
      deriveFinalizeIdempotencyKey({ ...input, manifestHash: `sha256:${'b'.repeat(64)}` }),
    ];
    expect(new Set([key, ...variants]).size).toBe(variants.length + 1);
  });

  // The documented pre-image: a versioned JSON array in a fixed order (contracts/cli.md §11).
  it('matches the documented pre-image', () => {
    const preImage = JSON.stringify([
      'evidgate.finalize-key.v1',
      runId,
      producerId,
      shard,
      7,
      7,
      manifestHash,
    ]);
    const expected = createHash('sha256').update(preImage, 'utf8').digest('hex');
    expect(keyVersion).toBe('evidgate.finalize-key.v1');
    expect(deriveFinalizeIdempotencyKey(input)).toBe(expected);
  });
});
