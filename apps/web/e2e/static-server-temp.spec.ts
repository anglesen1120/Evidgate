import { expect, test } from '@playwright/test';
import { writeFileSync } from 'node:fs';
import { connect } from 'node:net';
import { join } from 'node:path';

import {
  IS_ROOT,
  expectPolicy,
  getTempServer,
  rawRequest,
  useSharedTempServer,
} from './static-server.support';

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

// 09.1.1-T10 (AC7): the policy paths that need a document this suite writes itself — two
