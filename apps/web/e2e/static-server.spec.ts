import { expect, test } from '@playwright/test';
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { chmodSync, mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { connect, createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// 09.1.1-T1.2: the static server has routing logic of its own, so it is tested directly and
// not only through the browser. 09.1.1-T6.1: the real dist is a localized bundle — one
// directory per locale and no root page — so the requests that need the real bundle go to the
// `webServer` Playwright starts from playwright.config.ts over `apps/web/dist/browser` and cover
// the locale routing; the rest start the SAME `e2e/static-server.mjs` on a port of their own over
// a temporary dist, because their fixtures — an unreadable file, a symlink leading out of the
// bundle — must never exist in a build. Those temporary dists keep the single layout
// (index.html directly in the served root), which must stay supported: it is what a build
// without `localize` emits.
//
// 09.1.1-T10 (AC7, ADR-0008 rule 4): the policy tests split the same way. Every status class, the
// nonce of a real document and the untouched asset bodies are asserted against the production
// bundle through the `webServer`; the cases that need a document this suite writes itself — two
// placeholders, a HEAD answer, a `connect-src` extension — start their own server.

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
  // The element carries `ngCspNonce` (09.1.1-T10), so only the tag itself is stable here.
  expect(html).toContain('<app-root');
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
  expect(html).toContain('<app-root');
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

// 09.1.1-T10 (AC7, ADR-0008 rule 4): the server answers with the app-origin Content Security
// Policy that `scripts/csp.mjs` builds out of `scripts/app-origin-csp.json`, and the nonce of an
// HTML answer's header is the one written into that answer's body. These requests go to the real
// dist through the `webServer` Playwright started: the policy is the one the deployed edge sends,
// and the localized documents are what Angular actually bootstraps.
const NONCE_PATTERN = /'nonce-([A-Za-z0-9+/]{22}==)'/;

/** The app root carrying this nonce: the build emits `ngcspnonce` (lower case), the source `ngCspNonce`. */
function rootNonceAttribute(nonce: string): RegExp {
  const escaped = nonce.replace(/[+/]/g, (character) => `\\${character}`);
  return new RegExp(`<app-root\\b[^>]*\\sngcspnonce="${escaped}"`, 'i');
}

/**
 * Assert the policy of one response and return it. The clauses named here are the ones the ADR
 * requires — nonce-based styles, Trusted Types for script, no escape hatch for either — so the
 * assertions stay readable instead of pinning the whole header text.
 */
function expectPolicy(headers: Record<string, string>): string {
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
function nonceOf(policy: string): string {
  const nonce = NONCE_PATTERN.exec(policy)?.[1];
  if (nonce === undefined) {
    throw new Error(`no nonce in the policy: ${policy}`);
  }
  return nonce;
}

test('carries the app-origin policy on every status class', async ({ request }) => {
  const answers = [
    await request.get('/vi/'), // 200: a document of the real bundle
    await request.get('/vi/missing.js'), // 404: an asset a broken reference asks for
    await request.get('/', { maxRedirects: 0 }), // 302: the locale redirect
    await request.post('/'), // 405: a method a static bundle does not answer
    await request.get('/..%2f..%2fpackage.json'), // 403: a traversal refused before any read
  ];

  expect(answers.map((answer) => answer.status())).toEqual([200, 404, 302, 405, 403]);
  for (const answer of answers) {
    expectPolicy(answer.headers());
    // `no-store` on every answer, not only on documents: the policy names a per-response nonce,
    // so a cache that replays any of these would replay a nonce the next header does not allow.
    expect(answer.headers()['cache-control']).toBe('no-store');
  }

  // 400: a malformed escape. Sent verbatim over a socket, because the `request` fixture
  // normalises the URL and cannot produce a path the server has to reject while decoding it.
  const raw = await rawRequest(realDistPort(), 'GET /%zz HTTP/1.1');
  expect(raw.split('\r\n')[0]).toContain('400');
  expectPolicy(rawHeaders(raw));
  expect(rawHeaders(raw)['cache-control']).toBe('no-store');
});

test('writes the nonce of its own policy header into each locale document', async ({ request }) => {
  for (const locale of ['vi', 'en']) {
    const response = await request.get(`/${locale}/`);
    expect(response.status(), locale).toBe(200);
    const nonce = nonceOf(expectPolicy(response.headers()));
    const html = await response.text();

    // Angular reads `ngCspNonce` at runtime to authorise the styles it writes itself, so the
    // placeholder must be gone and the value must be THIS response's nonce, not a fixed one.
    // The builder lower-cases the attribute name in the emitted HTML; HTML attributes are
    // case-insensitive, so the match is too.
    expect(html, locale).toMatch(rootNonceAttribute(nonce));
    expect(html, locale).not.toContain('__CSP_NONCE__');
  }
});

test('generates a fresh nonce for every response', async ({ request }) => {
  const first = await request.get('/vi/');
  const second = await request.get('/vi/');
  const firstNonce = nonceOf(expectPolicy(first.headers()));
  const secondNonce = nonceOf(expectPolicy(second.headers()));

  expect(secondNonce).not.toBe(firstNonce);
  // Each body carries the nonce of its own response, so the pair cannot be swapped or shared.
  expect(await first.text()).toMatch(rootNonceAttribute(firstNonce));
  expect(await second.text()).toMatch(rootNonceAttribute(secondNonce));
});

test('sends an HTML document no-store, so a nonce is never replayed from a cache', async ({
  request,
}) => {
  // The document itself and the SPA fallback: both carry a one-time nonce in the body.
  for (const path of ['/vi/', '/en/runs/2f1c9d3e-0b7a-4c55-9a12-6d0f4b8e1a77']) {
    const response = await request.get(path);
    expect(response.status(), path).toBe(200);
    expect(response.headers()['content-type'], path).toContain('text/html');
    expect(response.headers()['cache-control'], path).toContain('no-store');
  }
});

test('sends an asset with a policy but never writes a nonce into its body', async ({ request }) => {
  const shell = await request.get('/vi/');
  const nonce = nonceOf(expectPolicy(shell.headers()));
  const html = await shell.text();

  const script = /<script[^>]+src="([^"]+)"/.exec(html)?.[1];
  expect(script, 'the bundle script reference').toBeDefined();
  const stylesheet = /<link[^>]+href="([^"]+\.css)"/.exec(html)?.[1];
  expect(stylesheet, 'the bundle stylesheet reference').toBeDefined();
  const references = [script, stylesheet].filter(
    (reference): reference is string => reference !== undefined,
  );

  // Resolve the references the way a browser does: against the locale's own `<base href>`, so
  // the fetch goes to the file the document names and not to a guessed path.
  const baseHref = /<base href="([^"]+)"/.exec(html)?.[1] ?? '/vi/';
  const documentUrl = new URL(baseHref, 'http://127.0.0.1');
  for (const reference of references) {
    const path = new URL(reference, documentUrl).pathname;
    const asset = await request.get(path);
    expect(asset.status(), path).toBe(200);
    // The asset answer has a policy of its own; only its body must stay byte-identical.
    expectPolicy(asset.headers());
    const body = await asset.text();
    expect(body, path).not.toContain(nonce);
    expect(body, path).not.toContain('__CSP_NONCE__');
  }
});

const SERVER_FILE = join(__dirname, 'static-server.mjs');
/** The root override `static-server.mjs` reads; Playwright's `webServer` never sets it. */
const ROOT_ENV = 'EVIDGATE_WEB_E2E_ROOT';
/** The port playwright.config.ts picked for the suite's own server and stores for its workers. */
const PORT_ENV = 'EVIDGATE_WEB_E2E_PORT';
/** The `connect-src` extension `static-server.mjs` validates once at start. */
const CONNECT_SRC_ENV = 'EVIDGATE_WEB_CONNECT_SRC';

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

/**
 * Start `static-server.mjs` over a throwaway dist whose single-layout index.html is `indexHtml`.
 * `env` adds variables to the child's environment — the policy tests set
 * `EVIDGATE_WEB_CONNECT_SRC` this way — and a value there wins over the inherited one.
 *
 * A start that fails (no "serving" line, an early exit) kills the child and drops the temp dir
 * before rethrowing: the caller only gets a `TempServer` it can stop, so a failed start would
 * otherwise leave a process and a directory behind for the rest of the run.
 */
async function startDist(indexHtml: string, env: Record<string, string> = {}): Promise<TempServer> {
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
async function stopTempServer(server: TempServer): Promise<void> {
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

/** The headers of a raw answer, keyed the way Playwright's `headers()` keys them (lowercase). */
function rawHeaders(raw: string): Record<string, string> {
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
function realDistPort(): number {
  const port = Number(process.env[PORT_ENV]);
  if (!Number.isInteger(port) || port <= 0) {
    throw new Error(`${PORT_ENV} is unset: run the suite through Playwright`);
  }
  return port;
}

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

test('survives clients that hang up while an asset is opening', async ({ request }) => {
  const { port, url, tempDir, child } = getTempServer();
  writeFileSync(join(tempDir, 'dist', 'browser', 'big.js'), 'x'.repeat(256 * 1024));

  // Each socket sends a request and is destroyed at once. Whether the hang-up lands before the
  // read stream opens is a race the kernel decides, so many sockets make the bad ordering a
  // near certainty; without the guard the server exits on the first one that loses.
  await Promise.all(
    Array.from(
      { length: 300 },
      () =>
        new Promise<void>((resolveAbort) => {
          const socket = connect(port, '127.0.0.1', () => {
            socket.write('GET /big.js HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n');
            socket.destroy();
          });
          socket.once('error', () => undefined);
          socket.once('close', () => resolveAbort());
        }),
    ),
  );

  const after = await request.get(`${url}/big.js`);
  expect(child.exitCode).toBeNull();
  expect(after.status()).toBe(200);
  expect((await after.body()).length).toBe(256 * 1024);
});

test('answers 500 for a file it cannot read and keeps serving the bundle', async ({ request }) => {
  test.skip(IS_ROOT, 'chmod 000 does not deny reads to root, so the 500 path cannot be produced');
  const { url } = getTempServer();

  const response = await request.get(`${url}/locked.txt`);
  expect(response.status()).toBe(500);
  expect(await response.text()).toBe('Internal Server Error');
  // The 500 is an answer like any other: it carries its own policy and its own nonce.
  expectPolicy(response.headers());
  expect(response.headers()['cache-control']).toBe('no-store');

  // The document path fails in `sendDocument`, which reads the whole file before writing any
  // header: that 500 goes through `sendText` and must carry the same policy.
  const documentResponse = await request.get(`${url}/locked.html`);
  expect(documentResponse.status()).toBe(500);
  expect(await documentResponse.text()).toBe('Internal Server Error');
  expectPolicy(documentResponse.headers());
  expect(documentResponse.headers()['cache-control']).toBe('no-store');

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
  // A refusal carries the app-origin policy and the no-store contract like every other answer.
  expectPolicy(response.headers());
  expect(response.headers()['cache-control']).toBe('no-store');
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

// 09.1.1-T10 (AC7): the policy paths that need a document this suite writes itself — two
// placeholders, a HEAD answer, a `connect-src` extension — run on their own server over a
// throwaway dist, so they never depend on the production bundle's own content.
test('replaces every placeholder of a document with the nonce of its own header', async ({
  request,
}) => {
  const server = await startDist(
    '<!doctype html><html><body><app-root ngCspNonce="__CSP_NONCE__"></app-root>' +
      '<noscript data-nonce="__CSP_NONCE__"></noscript></body></html>',
  );
  try {
    const document = await request.get(server.url);
    expect(document.status()).toBe(200);
    const nonce = nonceOf(expectPolicy(document.headers()));
    const html = await document.text();
    expect(html).not.toContain('__CSP_NONCE__');
    // Two placeholders, both replaced, both with this response's nonce: an injection that hit
    // only the first occurrence would leave the second one dead.
    expect(html.split(nonce).length - 1, 'every placeholder carries the nonce').toBe(2);

    // The SPA fallback serves the same file, and it is a different response with a new nonce.
    const fallback = await request.get(`${server.url}/runs/2f1c9d3e-0b7a-4c55-9a12-6d0f4b8e1a77`);
    expect(fallback.status()).toBe(200);
    const fallbackNonce = nonceOf(expectPolicy(fallback.headers()));
    expect(fallbackNonce).not.toBe(nonce);
    const fallbackHtml = await fallback.text();
    expect(fallbackHtml).not.toContain('__CSP_NONCE__');
    expect(fallbackHtml.split(fallbackNonce).length - 1).toBe(2);
  } finally {
    await stopTempServer(server);
  }
});

test('answers HEAD with the headers of its own response and no body', async () => {
  const server = await startDist(
    '<!doctype html><html><body><app-root ngCspNonce="__CSP_NONCE__"></app-root></body></html>',
  );
  try {
    // Raw sockets: the body length and the absence of a body are what a client library would
    // hide, and both are part of the contract of a document answer.
    const get = await rawRequest(server.port, 'GET / HTTP/1.1');
    const head = await rawRequest(server.port, 'HEAD / HTTP/1.1');
    const headHeaders = rawHeaders(head);

    expect(head.split('\r\n')[0]).toContain('200');
    expect(headHeaders['content-type']).toContain('text/html');
    expect(headHeaders['cache-control']).toContain('no-store');
    const headNonce = nonceOf(expectPolicy(headHeaders));
    expect(headNonce).not.toBe(nonceOf(expectPolicy(rawHeaders(get))));
    // A HEAD answer announces the length the GET body would have and sends none of it. Both
    // nonces have the same fixed length, so the document length is the same in both answers.
    const getBody = get.slice(get.indexOf('\r\n\r\n') + 4);
    expect(headHeaders['content-length']).toBe(String(Buffer.byteLength(getBody)));
    expect(head.slice(head.indexOf('\r\n\r\n') + 4)).toBe('');
  } finally {
    await stopTempServer(server);
  }
});

test('refuses to start when EVIDGATE_WEB_CONNECT_SRC is not a list of origins', async () => {
  const tempDir = mkdtempSync(join(tmpdir(), 'evidgate-web-e2e-'));
  try {
    const distDir = join(tempDir, 'dist', 'browser');
    mkdirSync(distDir, { recursive: true });
    writeFileSync(join(distDir, 'index.html'), '<!doctype html><html><body></body></html>');

    // A wildcard would open `connect-src` to every host, and a port outside 1-65535 is not a
    // port a browser can reach: both stop the server at start with the variable named, instead
    // of weakening the policy of every later response or shipping a dead source.
    for (const value of ['*', 'https://api.example.com:99999']) {
      const child = spawn(process.execPath, [SERVER_FILE, String(await freePort())], {
        env: { ...process.env, [ROOT_ENV]: distDir, [CONNECT_SRC_ENV]: value },
      });
      const { code, stderr } = await runToExit(child);

      expect(code, value).toBe(1);
      expect(stderr, value).toContain(CONNECT_SRC_ENV);
      expect(stderr, value).toContain(`invalid connect-src entry ${JSON.stringify(value)}`);
    }
  } finally {
    rmSync(tempDir, { recursive: true, force: true });
  }
});

test('appends a validated EVIDGATE_WEB_CONNECT_SRC origin to connect-src', async ({ request }) => {
  const server = await startDist('<!doctype html><html><body><app-root></app-root></body></html>', {
    [CONNECT_SRC_ENV]: 'https://api.example.com',
  });
  try {
    const policy = expectPolicy((await request.get(server.url)).headers());
    expect(policy).toContain("connect-src 'self' https://api.example.com");
  } finally {
    await stopTempServer(server);
  }
});
