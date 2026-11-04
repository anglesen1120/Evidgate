// 04.3.3-T2.2: the end-to-end proof (AC5–AC7). ONE case drives the real `playwright test` CLI
// on the fixture project under `test-fixtures/attach/` (the reporter loaded by its package
// name, exactly as a customer config loads it) against a tiny local `node:http` server that
// plays BOTH sides of the artifact path on an EPHEMERAL loopback port:
//   POST /v1/artifacts/upload-url  → the mint (a presigned loopback URL + signed headers),
//   PUT  /store/<id>?X-Amz-Signature=… → the object store,
//   POST /v1/artifacts/<id>/complete   → the commit.
// The bearer comes from the CLI's own dev token source (`EVIDGATE_ENV=dev` + a loopback API
// URL), so nothing here stubs the uploader: `uploadFile()` runs for real, including the
// two-pass hash and the streamed PUT.
//
// Harness idioms come from `reporter.e2e.test.ts`: compile the package first so the fixture
// resolves a current `dist`, run the CLI as a child process, then read the spool back. The
// assertions are the two things only a real run can show: `test.finished` carries the ids the
// SERVER issued, and neither the token nor the presigned URL ever reached the spool.
import { execFileSync, spawn } from 'node:child_process';
import { createServer, type Server, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import { mkdtempSync, readFileSync, readdirSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { afterAll, beforeAll, describe, expect, it } from 'vitest';

// The `/v1/ci/token` answer shape is shared with the CLI's own specs; the helper is test-only
// (excluded from the cli build), so it is imported from its source rather than `@evidgate/cli`.
import { exchangeBody } from '../../cli/src/commands/devExchange.testing.js';

import type { AttachmentRecord } from './attach.js';
import type { EventEnvelope } from './envelope.js';

const packageDir = join(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = join(packageDir, '..', '..');
const fixtureDir = join(packageDir, 'test-fixtures', 'attach');
const playwrightCli = join(repoRoot, 'node_modules', '@playwright', 'test', 'cli.js');
const tscBin = join(repoRoot, 'node_modules', 'typescript', 'bin', 'tsc');

const RUN_ID = 'run-e2e-attach';
const PRODUCER_ID = 'pw-attach-0';
/** Assembled at runtime so no token-shaped literal is stored in the spec (T78). */
const devToken = ['evidgate', 'attach', 'e2e', 'dev'].join('-');
/** The `ci_producer` token the dev token is exchanged for; the API calls carry this one. */
const mintedToken = ['evidgate', 'attach', 'e2e', 'minted'].join('-');
const EXECUTION_KEY = 'c3'.repeat(32);

const TRACE_ARTIFACT = '66666666-6666-4666-8666-666666666666';
const SCREENSHOT_ARTIFACT = '77777777-7777-4777-8777-777777777777';
/** The ids the fake server issues, keyed by kind so the assertion order is deterministic. */
const ARTIFACT_IDS = new Map<string, string>([
  ['trace', TRACE_ARTIFACT],
  ['screenshot', SCREENSHOT_ARTIFACT],
]);

interface Store {
  /** Kind of every mint, with the bearer the API call carried (null when absent). */
  mints: Array<{ kind: string; bearer: string | null }>;
  /** Authorization header of every presigned PUT (null when, as required, absent). */
  putAuth: Array<string | null>;
  exchangeBodies: string[];
  /** Artifact ids that were committed. */
  completes: string[];
  /** The exact bytes the store received per artifact id. */
  stored: Map<string, number>;
}

let spool = '';
let outputDir = '';
let cacheDir = '';
let server: Server | undefined;
let cliOutput = '';
let runFailure: string | null = null;
const store: Store = {
  mints: [],
  putAuth: [],
  exchangeBodies: [],
  completes: [],
  stored: new Map(),
};

/** The mint body: the signed size and checksum are the true ones for the request's file. */
function uploadUrlBody(input: {
  readonly artifactId: string;
  readonly origin: string;
  readonly bytes: number;
  readonly sha256: string;
}): Record<string, unknown> {
  const digest = Buffer.from(input.sha256.slice('sha256:'.length), 'hex').toString('base64');
  return {
    artifact_id: input.artifactId,
    object_key: `pending/org/${input.artifactId}`,
    upload_url: `${input.origin}/store/${input.artifactId}?X-Amz-Signature=deadbeefcafe`,
    required_headers: {
      'Content-Length': String(input.bytes),
      'x-amz-checksum-sha256': digest,
    },
    expires_at: new Date(Date.now() + 600_000).toISOString(),
  };
}

function sendJson(response: ServerResponse, status: number, body: unknown): void {
  const payload = Buffer.from(JSON.stringify(body), 'utf8');
  response.writeHead(status, {
    'content-type': 'application/json',
    'content-length': String(payload.byteLength),
  });
  response.end(payload);
}

beforeAll(async () => {
  // The fixture resolves the package by name, so the compiled entry must exist and be
  // current; `tsc -b` is incremental (`reporter.e2e.test.ts` does the same).
  execFileSync(process.execPath, [tscBin, '-b'], { cwd: packageDir, stdio: 'pipe' });
  spool = mkdtempSync(join(tmpdir(), 'evidgate-attach-e2e-'));
  outputDir = mkdtempSync(join(tmpdir(), 'evidgate-attach-e2e-out-'));
  cacheDir = mkdtempSync(join(tmpdir(), 'evidgate-attach-e2e-cache-'));

  const listening = createServer((request, response) => {
    const chunks: Buffer[] = [];
    request.on('data', (chunk: Buffer | string) => {
      chunks.push(typeof chunk === 'string' ? Buffer.from(chunk) : chunk);
    });
    request.on('end', () => {
      const body = Buffer.concat(chunks);
      const url = new URL(request.url ?? '/', 'http://127.0.0.1');
      const authorization = request.headers.authorization ?? null;
      if (request.method === 'POST' && url.pathname === '/v1/ci/token') {
        store.exchangeBodies.push(body.toString('utf8'));
        sendJson(
          response,
          200,
          exchangeBody(mintedToken, {
            expiresAt: new Date(Date.now() + 30 * 60_000).toISOString(),
            projectId: '0198a000-0000-7000-8000-00000000d002',
          }),
        );
        return;
      }
      if (request.method === 'POST' && url.pathname === '/v1/artifacts/upload-url') {
        const mint = JSON.parse(body.toString('utf8')) as {
          kind?: unknown;
          bytes?: unknown;
          sha256?: unknown;
        };
        const kind = typeof mint.kind === 'string' ? mint.kind : '';
        const artifactId = ARTIFACT_IDS.get(kind);
        const bytes = typeof mint.bytes === 'number' ? mint.bytes : 0;
        const sha256 = typeof mint.sha256 === 'string' ? mint.sha256 : '';
        store.mints.push({ kind, bearer: authorization });
        if (artifactId === undefined) {
          sendJson(response, 422, { error: { code: 'unsupported_kind' } });
          return;
        }
        store.stored.set(artifactId, bytes);
        const origin = `http://127.0.0.1:${String(serverPort())}`;
        sendJson(response, 201, uploadUrlBody({ artifactId, origin, bytes, sha256 }));
        return;
      }
      if (request.method === 'PUT' && url.pathname.startsWith('/store/')) {
        store.putAuth.push(authorization);
        response.writeHead(200, { 'content-length': '0' });
        response.end();
        return;
      }
      if (request.method === 'POST' && url.pathname.endsWith('/complete')) {
        const artifactId = url.pathname.split('/').at(-2) ?? '';
        store.completes.push(artifactId);
        sendJson(response, 200, {
          artifact_id: artifactId,
          state: 'ready',
          bytes: store.stored.get(artifactId) ?? 0,
          scan_state: 'pending',
          failure_code: null,
        });
        return;
      }
      sendJson(response, 404, { error: { code: 'not_found' } });
    });
  });
  server = listening;
  await new Promise<void>((resolve, reject) => {
    listening.once('error', reject);
    listening.listen(0, '127.0.0.1', resolve);
  });

  const run = await runPlaywright({
    ...process.env,
    EVIDGATE_SPOOL_DIR: spool,
    EVIDGATE_RUN_ID: RUN_ID,
    EVIDGATE_E2E_OUTPUT_DIR: outputDir,
    EVIDGATE_E2E_EXECUTION_KEY: EXECUTION_KEY,
    EVIDGATE_API_URL: `http://127.0.0.1:${String(serverPort())}`,
    EVIDGATE_PROJECT: 'proj-e2e',
    EVIDGATE_ENV: 'dev',
    EVIDGATE_DEV_TOKEN: devToken,
    EVIDGATE_CACHE_DIR: cacheDir,
  });
  cliOutput = run.output;
  if (run.status !== 0) {
    runFailure = `playwright test exited with status ${String(run.status)}: ${cliOutput}`;
  }
}, 180_000);

afterAll(() => {
  const closing = server;
  server = undefined;
  closing?.close();
  for (const dir of [spool, outputDir, cacheDir]) {
    if (dir.length > 0) rmSync(dir, { recursive: true, force: true });
  }
});

/**
 * Runs the fixture through the real CLI and resolves once the child is gone. ASYNC on
 * purpose: the artifact server lives in THIS process, so a synchronous spawn would block the
 * event loop that has to answer the child's `upload-url`, PUT and `complete` calls.
 */
async function runPlaywright(env: NodeJS.ProcessEnv): Promise<{ status: number; output: string }> {
  const child = spawn(
    process.execPath,
    [playwrightCli, 'test', '--config', join(fixtureDir, 'playwright.config.mjs')],
    { cwd: fixtureDir, env, stdio: ['ignore', 'pipe', 'pipe'] },
  );
  const chunks: Buffer[] = [];
  child.stdout.on('data', (chunk: Buffer) => {
    chunks.push(chunk);
  });
  child.stderr.on('data', (chunk: Buffer) => {
    chunks.push(chunk);
  });
  const kill = setTimeout(() => {
    child.kill('SIGKILL');
  }, 120_000);
  const status = await new Promise<number>((resolve) => {
    child.once('close', (code) => {
      resolve(code ?? 1);
    });
  });
  clearTimeout(kill);
  return { status, output: Buffer.concat(chunks).toString('utf8') };
}

/** The port the ephemeral server bound (0 before it listened, which the handler never sees). */
function serverPort(): number {
  const address: AddressInfo | string | null = server?.address() ?? null;
  return typeof address === 'object' && address !== null ? address.port : 0;
}

function spoolDir(): string {
  return join(spool, RUN_ID, PRODUCER_ID);
}

function spoolLines(): string[] {
  const lines: string[] = [];
  for (const entry of readdirSync(spoolDir())
    .filter((name) => name.endsWith('.jsonl'))
    .sort()) {
    for (const line of readFileSync(join(spoolDir(), entry), 'utf8').split('\n')) {
      if (line.length > 0) lines.push(line);
    }
  }
  return lines;
}

/** Every regular file under the spool root, as text (the secret scan reads these). */
function spoolText(): string[] {
  const texts: string[] = [];
  const walk = (directory: string): void => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      if (entry.isDirectory()) {
        walk(path);
        continue;
      }
      if (entry.isFile() && statSync(path).size > 0) {
        texts.push(readFileSync(path, 'utf8'));
      }
    }
  };
  walk(spool);
  return texts;
}

function recordsOf(envelope: EventEnvelope | undefined): AttachmentRecord[] {
  const value: unknown = envelope?.ext.attachments;
  // The pipeline built these records and the `ext` member is open, so the shape is ours.
  return Array.isArray(value) ? (value as AttachmentRecord[]) : [];
}

describe('attachment upload e2e (real playwright test CLI)', () => {
  it('uploads both attachments and references the server ids without leaking a secret', () => {
    if (runFailure !== null) throw new Error(`playwright test failed: ${runFailure}`);
    expect(cliOutput).toContain('1 passed');

    const envelopes = spoolLines().map((line) => JSON.parse(line) as EventEnvelope);
    const finished = envelopes.filter((item) => item.type === 'test.finished');
    expect(finished).toHaveLength(1);
    const envelope = finished[0];
    expect(envelope?.status).toBe('passed');
    expect(envelope?.execution_key).toBe(EXECUTION_KEY);
    // AC5: the ids are the server's own, in attachment order (trace, then screenshot).
    expect(envelope?.artifact_ids).toEqual([TRACE_ARTIFACT, SCREENSHOT_ARTIFACT]);
    const records = recordsOf(envelope);
    expect(records.map((record) => [record.kind, record.outcome])).toEqual([
      ['trace', 'uploaded'],
      ['screenshot', 'uploaded'],
    ]);
    // Playwright rewrites an attachment's file name to `<slug>-<sha1>.<ext>`, so the record
    // carries that BASE name (never the absolute path the result reported).
    const traceRecord = records[0];
    const screenshotRecord = records[1];
    expect(traceRecord?.name.startsWith('trace-attach')).toBe(true);
    expect(traceRecord?.name.endsWith('.zip')).toBe(true);
    expect(screenshotRecord?.name.startsWith('screenshot-attach')).toBe(true);
    expect(screenshotRecord?.name.endsWith('.png')).toBe(true);
    expect(records.every((record) => !record.name.includes('/'))).toBe(true);
    expect(records.map((record) => record.artifact_id)).toEqual([
      TRACE_ARTIFACT,
      SCREENSHOT_ARTIFACT,
    ]);
    // AC7: a trace is sensitive by default; a screenshot is only sensitive on annotation.
    expect(records.map((record) => record.sensitive)).toEqual([true, false]);

    // The API calls carried the CI bearer; the presigned PUT never did (§8 S3).
    expect(store.mints.map((mint) => mint.kind).sort()).toEqual(['screenshot', 'trace']);
    expect(store.mints.every((mint) => mint.bearer === `Bearer ${mintedToken}`)).toBe(true);
    expect(store.exchangeBodies).toEqual([JSON.stringify({ ingest_token: devToken })]);
    expect(store.putAuth).toHaveLength(2);
    expect(store.putAuth.every((value) => value === null)).toBe(true);
    expect(store.completes.sort()).toEqual([SCREENSHOT_ARTIFACT, TRACE_ARTIFACT].sort());

    // Neither the token nor the presigned URL (or its signature) reached the spool.
    for (const text of spoolText()) {
      expect(text).not.toContain(devToken);
      expect(text).not.toContain(mintedToken);
      expect(text).not.toContain('X-Amz-Signature');
      expect(text).not.toContain('/store/');
    }
    // Sequences stay contiguous from 1 even with the held batches.
    expect(envelopes.map((item) => item.sequence)).toEqual(envelopes.map((_, index) => index + 1));
  });
});
