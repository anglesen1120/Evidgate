import { expect, test } from '@playwright/test';

import { writeFileSync } from 'node:fs';
import { connect } from 'node:net';
import { join } from 'node:path';

import {
  IS_ROOT,
  expectPolicy,
  getTempServer,
  nonceOf,
  rawHeaders,
  rawRequest,
  realDistPort,
  useSharedTempServer,
} from './static-server.support';

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

/** The app root carrying this nonce: the build emits `ngcspnonce` (lower case), the source `ngCspNonce`. */
function rootNonceAttribute(nonce: string): RegExp {
  const escaped = nonce.replace(/[+/]/g, (character) => `\\${character}`);
  return new RegExp(`<app-root\\b[^>]*\\sngcspnonce="${escaped}"`, 'i');
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

// The cases that share one temporary server over a throwaway dist (see static-server.support.ts).
useSharedTempServer();

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

test('answers 405 with the allowed methods for a write request', async ({ request }) => {
  const { url } = getTempServer();

  const response = await request.post(`${url}/`);
  expect(response.status()).toBe(405);
  expect(response.headers()['allow']).toBe('GET, HEAD');
});
