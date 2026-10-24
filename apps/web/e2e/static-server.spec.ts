import { expect, test } from '@playwright/test';
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { chmodSync, mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { connect, createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// 09.1.1-T1.2: the static server has routing logic of its own, so it is tested directly and
// not only through the browser. 09.1.1-T6.1: the real dist is a localized bundle — one
// directory per locale and no root page — so the first four requests go to the `webServer`
// Playwright starts from playwright.config.ts over `apps/web/dist/browser` and cover the
// locale routing; the rest start the SAME `e2e/static-server.mjs` on a port of their own over
// a temporary dist, because their fixtures — an unreadable file, a symlink leading out of the
// bundle — must never exist in a build. Those temporary dists keep the single layout
// (index.html directly in the served root), which must stay supported: it is what a build
// without `localize` emits.

test('redirects / to the default locale (vi)', async ({ request }) => {
  const response = await request.get('/', { maxRedirects: 0 });

  expect(response.status()).toBe(302);
  expect(response.headers()['location']).toBe('/vi/');
});

test('serves the vi index.html for a client route under /vi (SPA fallback)', async ({
  request,
}) => {
  const response = await request.get('/vi/runs/2f1c9d3e-0b7a-4c55-9a12-6d0f4b8e1a77');

  expect(response.status()).toBe(200);
  expect(response.headers()['content-type']).toContain('text/html');
  const html = await response.text();
  expect(html).toContain('<app-root></app-root>');
  // The locale bundle carries its own document language, so the fallback is that locale's page.
  expect(html).toContain('lang="vi"');
});

test('serves the en index.html for a client route under /en (SPA fallback)', async ({
  request,
}) => {
  const response = await request.get('/en/runs/2f1c9d3e-0b7a-4c55-9a12-6d0f4b8e1a77');

  expect(response.status()).toBe(200);
  expect(response.headers()['content-type']).toContain('text/html');
  const html = await response.text();
  expect(html).toContain('<app-root></app-root>');
  expect(html).toContain('lang="en"');
});

test('treats a first segment that is no locale as an ordinary SPA path', async ({ request }) => {
  // Documented behaviour of the locale layout: only the directories the bundle actually has
  // are locales, and the root holds no page, so such a path falls back to the default locale.
  const response = await request.get('/zz/runs/2f1c9d3e-0b7a-4c55-9a12-6d0f4b8e1a77');

  expect(response.status()).toBe(200);
  expect(response.headers()['content-type']).toContain('text/html');
  expect(await response.text()).toContain('lang="vi"');
});

test('rejects a traversal path instead of reading outside dist/browser', async ({ request }) => {
  // `%2f` keeps the escape encoded through URL normalisation, so the server itself must
  // decode and reject it; the body must never be a workspace file such as package.json.
  const response = await request.get('/..%2f..%2fpackage.json');

  expect(response.status()).toBe(403);
  expect(await response.text()).not.toContain('@evidgate/web');
});

const SERVER_FILE = join(__dirname, 'static-server.mjs');
/** The root override `static-server.mjs` reads; Playwright's `webServer` never sets it. */
const ROOT_ENV = 'EVIDGATE_WEB_E2E_ROOT';

// `chmod 000` is no read barrier for root, so the 500 case cannot be produced when the suite
// runs as uid 0; the test says so and skips instead of asserting a false failure.
const IS_ROOT = typeof process.getuid === 'function' && process.getuid() === 0;

interface TempServer {
  readonly url: string;
  readonly port: number;
  readonly child: ChildProcessWithoutNullStreams;
  readonly tempDir: string;
}

let tempServer: TempServer | undefined;

/** A port the OS has just handed out and taken back again: the server itself refuses port 0. */
async function freePort(): Promise<number> {
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

function waitUntilServing(child: ChildProcessWithoutNullStreams): Promise<void> {
  return new Promise((resolveServing, rejectServing) => {
    let stderr = '';
    child.stderr.setEncoding('utf8');
    child.stderr.on('data', (chunk: string) => {
      stderr += chunk;
    });
    child.once('error', rejectServing);
    child.once('exit', (code) => {
      rejectServing(new Error(`static-server.mjs exited (${String(code)}): ${stderr}`));
    });
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', (chunk: string) => {
      if (chunk.includes('serving')) {
        resolveServing();
      }
    });
  });
}

/** Run the server until it exits and report what it said; used for a dist it must refuse. */
async function runToExit(
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

async function startTempServer(): Promise<TempServer> {
  const tempDir = mkdtempSync(join(tmpdir(), 'evidgate-web-e2e-'));
  const distDir = join(tempDir, 'dist', 'browser');
  mkdirSync(distDir, { recursive: true });
  writeFileSync(
    join(distDir, 'index.html'),
    '<!doctype html><html><body><app-root></app-root></body></html>',
  );
  // Stat-able but not readable: the read stream fails, the server must answer 500.
  writeFileSync(join(distDir, 'locked.txt'), 'never served');
  if (!IS_ROOT) {
    chmodSync(join(distDir, 'locked.txt'), 0o000);
  }
  // A link that leads out of the bundle: served only if the server trusted the link.
  const outsideFile = join(tempDir, 'outside-the-bundle.txt');
  writeFileSync(outsideFile, 'outside the bundle');
  symlinkSync(outsideFile, join(distDir, 'escape.txt'));

  const port = await freePort();
  // `EVIDGATE_WEB_E2E_ROOT` points the server at the temporary dist; the port goes as the
  // first argument, which wins over the `EVIDGATE_WEB_E2E_PORT` value the config set for the
  // suite's own server and that this child inherits.
  const child = spawn(process.execPath, [SERVER_FILE, String(port)], {
    env: { ...process.env, [ROOT_ENV]: distDir },
  });
  await waitUntilServing(child);
  return { url: `http://127.0.0.1:${port}`, port, child, tempDir };
}

function getTempServer(): TempServer {
  if (tempServer === undefined) {
    throw new Error('the temporary static server was not started');
  }
  return tempServer;
}

/**
 * Send a request line verbatim over a socket: Playwright's `request` fixture validates the URL
 * and cannot produce the malformed line this test needs. Resolves with the raw response text.
 */
async function rawRequest(port: number, requestLine: string): Promise<string> {
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

test.beforeAll(async () => {
  tempServer = await startTempServer();
});

test.afterAll(async () => {
  if (tempServer === undefined) {
    return;
  }
  const { child, tempDir } = tempServer;
  tempServer = undefined;
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
});

test('survives a malformed request line and answers it with 400', async ({ request }) => {
  const { port, url } = getTempServer();

  // `//[` is parsed protocol-relative, so `[` is an invalid host and `new URL` throws; the
  // throw must be answered, not thrown out of the request listener.
  const raw = await rawRequest(port, 'GET //[ HTTP/1.1');
  expect(raw.split('\r\n')[0]).toContain('400');

  // Same process, still listening: the next request is served.
  const after = await request.get(`${url}/`);
  expect(after.status()).toBe(200);
});

test('answers 500 for a file it cannot read and keeps serving the bundle', async ({ request }) => {
  test.skip(IS_ROOT, 'chmod 000 does not deny reads to root, so the 500 path cannot be produced');
  const { url } = getTempServer();

  const response = await request.get(`${url}/locked.txt`);
  expect(response.status()).toBe(500);
  expect(await response.text()).toBe('Internal Server Error');

  expect((await request.get(`${url}/`)).status()).toBe(200);
});

test('answers 404 for a missing asset of any type and falls back only for routes', async ({
  request,
}) => {
  const { url } = getTempServer();

  // Known type, font type this server does not map, and a type it has never heard of: every
  // final segment with an extension is an asset request, so a missing one is a 404.
  for (const path of ['/missing-logo.png', '/fonts/missing.woff', '/site.webmanifest']) {
    const response = await request.get(`${url}${path}`);
    expect(response.status(), path).toBe(404);
    expect(await response.text()).toBe('Not Found');
  }

  const route = await request.get(`${url}/some/route`);
  expect(route.status()).toBe(200);
  expect(route.headers()['content-type']).toContain('text/html');
  expect(await route.text()).toContain('<app-root></app-root>');
});

test('refuses a symlink that leads out of the bundle', async ({ request }) => {
  const { url } = getTempServer();

  const response = await request.get(`${url}/escape.txt`);
  expect(response.status()).toBe(403);
  expect(await response.text()).not.toContain('outside the bundle');
});

test('refuses to start when index.html is a link out of the bundle', async () => {
  const tempDir = mkdtempSync(join(tmpdir(), 'evidgate-web-e2e-'));
  try {
    const distDir = join(tempDir, 'dist', 'browser');
    mkdirSync(distDir, { recursive: true });
    const outsidePage = join(tempDir, 'outside-index.html');
    writeFileSync(outsidePage, '<!doctype html><html><body>outside the bundle</body></html>');
    symlinkSync(outsidePage, join(distDir, 'index.html'));

    const child = spawn(process.execPath, [SERVER_FILE, String(await freePort())], {
      env: { ...process.env, [ROOT_ENV]: distDir },
    });
    const { code, stderr } = await runToExit(child);

    // The fallback page is the one file every client route answers with, so an escaping link
    // there is a refused bundle rather than a served file.
    expect(code).toBe(1);
    expect(stderr).toContain('does not resolve to a file inside');
  } finally {
    rmSync(tempDir, { recursive: true, force: true });
  }
});

test('refuses to start when a locale index.html is a link out of the bundle', async () => {
  const tempDir = mkdtempSync(join(tmpdir(), 'evidgate-web-e2e-'));
  try {
    const distDir = join(tempDir, 'dist', 'browser');
    mkdirSync(join(distDir, 'vi'), { recursive: true });
    const outsidePage = join(tempDir, 'outside-index.html');
    writeFileSync(outsidePage, '<!doctype html><html><body>outside the bundle</body></html>');
    symlinkSync(outsidePage, join(distDir, 'vi', 'index.html'));

    const child = spawn(process.execPath, [SERVER_FILE, String(await freePort())], {
      env: { ...process.env, [ROOT_ENV]: distDir },
    });
    const { code, stderr } = await runToExit(child);

    // Every client route of that locale answers with this file, so it is checked the same way
    // as the single-layout index.html above.
    expect(code).toBe(1);
    expect(stderr).toContain('does not resolve to a file inside');
  } finally {
    rmSync(tempDir, { recursive: true, force: true });
  }
});

test('answers 405 with the allowed methods for a write request', async ({ request }) => {
  const { url } = getTempServer();

  const response = await request.post(`${url}/`);
  expect(response.status()).toBe(405);
  expect(response.headers()['allow']).toBe('GET, HEAD');
});
