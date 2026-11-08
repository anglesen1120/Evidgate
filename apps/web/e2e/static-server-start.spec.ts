import { expect, test } from '@playwright/test';
import { spawn } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import {
  CONNECT_SRC_ENV,
  ROOT_ENV,
  SERVER_FILE,
  expectPolicy,
  freePort,
  nonceOf,
  rawHeaders,
  rawRequest,
  runToExit,
  startDist,
  stopTempServer,
} from './static-server.support';

// The cases that start a server of their own: a dist it must refuse, a document this suite writes
// itself and a `connect-src` extension (see static-server.support.ts).

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
