import { expect, test } from '@playwright/test';
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { chmodSync, mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { connect, createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// Shared by the static-server specs: a temporary server over a throwaway dist, raw-socket
// requests and the policy assertions. Not a spec itself, so Playwright never collects it.

const NONCE_PATTERN = /'nonce-([A-Za-z0-9+/]{22}==)'/;

/**
 * Assert the policy of one response and return it. The clauses named here are the ones the ADR
 * requires — nonce-based styles, Trusted Types for script, no escape hatch for either — so the
 * assertions stay readable instead of pinning the whole header text.
 */
export function expectPolicy(headers: Record<string, string>): string {
  const policy = headers['content-security-policy'];
  if (policy === undefined) {
    throw new Error(`no content-security-policy header in ${JSON.stringify(headers)}`);
  }
  expect(policy).toContain("script-src 'self'");
  expect(policy).toContain("style-src 'self' 'nonce-");
  expect(policy).toContain("require-trusted-types-for 'script'");
  expect(policy).toContain('trusted-types angular');
  expect(policy).not.toContain('unsafe-');
  expect(policy).not.toContain('angular#unsafe-bypass');
  return policy;
}

/** The nonce of one response, read out of that response's own policy header. */
export function nonceOf(policy: string): string {
  const nonce = NONCE_PATTERN.exec(policy)?.[1];
  if (nonce === undefined) {
    throw new Error(`no nonce in the policy: ${policy}`);
  }
  return nonce;
}

export const SERVER_FILE = join(__dirname, 'static-server.mjs');
/** The root override `static-server.mjs` reads; Playwright's `webServer` never sets it. */
export const ROOT_ENV = 'EVIDGATE_WEB_E2E_ROOT';
/** The port playwright.config.ts picked for the suite's own server and stores for its workers. */
export const PORT_ENV = 'EVIDGATE_WEB_E2E_PORT';
/** The `connect-src` extension `static-server.mjs` validates once at start. */
export const CONNECT_SRC_ENV = 'EVIDGATE_WEB_CONNECT_SRC';

// `chmod 000` is no read barrier for root, so the 500 case cannot be produced when the suite
// runs as uid 0; the test says so and skips instead of asserting a false failure.
export const IS_ROOT = typeof process.getuid === 'function' && process.getuid() === 0;

export interface TempServer {
  readonly url: string;
  readonly port: number;
  readonly child: ChildProcessWithoutNullStreams;
  readonly tempDir: string;
}

let tempServer: TempServer | undefined;

/** A port the OS has just handed out and taken back again: the server itself refuses port 0. */
export async function freePort(): Promise<number> {
  const probe = createServer();
  const port = await new Promise<number>((resolvePort, rejectPort) => {
    probe.once('error', rejectPort);
    probe.listen(0, '127.0.0.1', () => {
      const address = probe.address();
      resolvePort(typeof address === 'object' && address !== null ? address.port : 0);
    });
  });
  await new Promise<void>((resolveClose) => probe.close(() => resolveClose()));
  if (port === 0) {
    throw new Error('could not reserve a port for the temporary static server');
  }
  return port;
}

/** How long a temporary server may take to print its "serving" line before the start fails. */
const START_TIMEOUT_MS = 10_000;

function waitUntilServing(child: ChildProcessWithoutNullStreams): Promise<void> {
  return new Promise((resolveServing, rejectServing) => {
    let stderr = '';
    // A child that never reaches its listen callback would hang the whole suite instead of
    // failing it, so the wait is bounded and the failure quotes what the child did say.
    const timer = setTimeout(() => {
      rejectServing(
        new Error(`static-server.mjs did not serve within ${START_TIMEOUT_MS} ms: ${stderr}`),
      );
    }, START_TIMEOUT_MS);
    child.stderr.setEncoding('utf8');
    child.stderr.on('data', (chunk: string) => {
      stderr += chunk;
    });
    child.once('error', (error) => {
      clearTimeout(timer);
      rejectServing(error);
    });
    child.once('exit', (code) => {
      clearTimeout(timer);
      rejectServing(new Error(`static-server.mjs exited (${String(code)}): ${stderr}`));
    });
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', (chunk: string) => {
      if (chunk.includes('serving')) {
        clearTimeout(timer);
        resolveServing();
      }
    });
  });
}

/** Run the server until it exits and report what it said; used for a dist it must refuse. */
export async function runToExit(
  child: ChildProcessWithoutNullStreams,
): Promise<{ code: number | null; stderr: string }> {
  let stderr = '';
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', (chunk: string) => {
    stderr += chunk;
  });
  const code = await new Promise<number | null>((resolveExit) => child.once('exit', resolveExit));
  return { code, stderr };
}

/**
 * Start `static-server.mjs` over a throwaway dist whose single-layout index.html is `indexHtml`.
 * `env` adds variables to the child's environment — the policy tests set
 * `EVIDGATE_WEB_CONNECT_SRC` this way — and a value there wins over the inherited one.
 *
 * A start that fails (no "serving" line, an early exit) kills the child and drops the temp dir
 * before rethrowing: the caller only gets a `TempServer` it can stop, so a failed start would
 * otherwise leave a process and a directory behind for the rest of the run.
 */
export async function startDist(
  indexHtml: string,
  env: Record<string, string> = {},
): Promise<TempServer> {
  const tempDir = mkdtempSync(join(tmpdir(), 'evidgate-web-e2e-'));
  let child: ChildProcessWithoutNullStreams | undefined;
  try {
    const distDir = join(tempDir, 'dist', 'browser');
    mkdirSync(distDir, { recursive: true });
    writeFileSync(join(distDir, 'index.html'), indexHtml);

    const port = await freePort();
    // `EVIDGATE_WEB_E2E_ROOT` points the server at the temporary dist; the port goes as the
    // first argument, which wins over the `EVIDGATE_WEB_E2E_PORT` value the config set for the
    // suite's own server and that this child inherits.
    child = spawn(process.execPath, [SERVER_FILE, String(port)], {
      env: { ...process.env, [ROOT_ENV]: distDir, ...env },
    });
    await waitUntilServing(child);
    return { url: `http://127.0.0.1:${port}`, port, child, tempDir };
  } catch (error) {
    // SIGKILL, not SIGTERM: a child that never reached its listen callback has no handler yet,
    // and a failed start must not wait out a grace period it will not use.
    child?.kill('SIGKILL');
    rmSync(tempDir, { recursive: true, force: true });
    throw error;
  }
}

/** Kill a temporary server and drop its dist; also safe for one that already exited. */
export async function stopTempServer(server: TempServer): Promise<void> {
  const { child, tempDir } = server;
  if (child.exitCode === null && child.signalCode === null) {
    child.kill('SIGTERM');
    // The server closes its listener on SIGTERM; a client socket that never ends must not hang
    // the suite, so the process is killed outright after a grace period.
    await Promise.race([
      new Promise<void>((resolveExit) => child.once('exit', () => resolveExit())),
      new Promise<void>((resolveTimeout) => setTimeout(resolveTimeout, 2_000)),
    ]);
    child.kill('SIGKILL');
  }
  rmSync(tempDir, { recursive: true, force: true });
}

async function startTempServer(): Promise<TempServer> {
  // No placeholder in this document on purpose: it proves an HTML answer without one is served
  // as it is, and the assertions below can keep naming `<app-root></app-root>`.
  const server = await startDist('<!doctype html><html><body><app-root></app-root></body></html>');
  const distDir = join(server.tempDir, 'dist', 'browser');
  // Stat-able but not readable: the read stream fails, the server must answer 500.
  writeFileSync(join(distDir, 'locked.txt'), 'never served');
  // The same failure for a DOCUMENT: `sendDocument` reads the whole file before writing a
  // header, so this path proves a 500 that still carries its own policy header.
  writeFileSync(
    join(distDir, 'locked.html'),
    '<!doctype html><html><body>never served</body></html>',
  );
  if (!IS_ROOT) {
    chmodSync(join(distDir, 'locked.txt'), 0o000);
    chmodSync(join(distDir, 'locked.html'), 0o000);
  }
  // A link that leads out of the bundle: served only if the server trusted the link.
  const outsideFile = join(server.tempDir, 'outside-the-bundle.txt');
  writeFileSync(outsideFile, 'outside the bundle');
  symlinkSync(outsideFile, join(distDir, 'escape.txt'));
  return server;
}

export function getTempServer(): TempServer {
  if (tempServer === undefined) {
    throw new Error('the temporary static server was not started');
  }
  return tempServer;
}

/**
 * Send a request line verbatim over a socket: Playwright's `request` fixture validates the URL
 * and cannot produce the malformed line this test needs. Resolves with the raw response text.
 */
export async function rawRequest(port: number, requestLine: string): Promise<string> {
  return new Promise((resolveText, rejectText) => {
    const socket = connect(port, '127.0.0.1');
    let text = '';
    socket.setEncoding('utf8');
    socket.on('data', (chunk: string) => {
      text += chunk;
    });
    socket.once('error', rejectText);
    socket.once('end', () => resolveText(text));
    socket.write(`${requestLine}\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n`);
  });
}

/** The headers of a raw answer, keyed the way Playwright's `headers()` keys them (lowercase). */
export function rawHeaders(raw: string): Record<string, string> {
  const [head = ''] = raw.split('\r\n\r\n');
  const headers: Record<string, string> = {};
  for (const line of head.split('\r\n').slice(1)) {
    const separator = line.indexOf(':');
    if (separator > 0) {
      headers[line.slice(0, separator).toLowerCase()] = line.slice(separator + 1).trim();
    }
  }
  return headers;
}

/**
 * The port playwright.config.ts gave the suite's own server. It is in `process.env` exactly
 * because the config stores it there for the forked workers, so a raw socket can reach the same
 * process without the fixture's URL normalisation.
 */
export function realDistPort(): number {
  const port = Number(process.env[PORT_ENV]);
  if (!Number.isInteger(port) || port <= 0) {
    throw new Error(`${PORT_ENV} is unset: run the suite through Playwright`);
  }
  return port;
}

/** Register the hooks that start and stop the one server the calling spec file shares. */
export function useSharedTempServer(): void {
  test.beforeAll(async () => {
    tempServer = await startTempServer();
  });

  test.afterAll(async () => {
    if (tempServer === undefined) {
      return;
    }
    const server = tempServer;
    tempServer = undefined;
    await stopTempServer(server);
  });
}
