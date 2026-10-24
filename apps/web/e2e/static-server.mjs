#!/usr/bin/env node
// 09.1.1-T1.2 (00 §5.6 W9, AC2): the static server the Playwright `webServer` in
// playwright.config.ts starts. It serves the PRODUCTION bundle built into
// `apps/web/dist/browser` — deliberately a plain Node http server instead of
// `ng serve` or a static-server package: the E2E gate must fail on a stale dist, must not
// depend on the dev server's live rebuild, and must add no dependency to apps/web.
//
// Two behaviours carry real logic and have their own specs in `e2e/static-server.spec.ts`:
//   - SPA fallback: a client route such as `/runs/<id>` is not a file, so it is answered
//     with `index.html` (200); a missing *asset* (`.js`, `.css`, …) is still a 404 so a
//     broken bundle reference cannot hide behind the fallback.
//   - traversal guard: the path is decoded, `..` segments are rejected outright, and the
//     resolved file is checked to stay inside the root — `/..%2f..%2fpackage.json` reads
//     nothing outside `dist/browser`.
import { createReadStream, existsSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(fileURLToPath(new URL('../dist/browser/', import.meta.url)));
const INDEX_FILE = join(ROOT, 'index.html');

/** Set by playwright.config.ts; a fixed default would reintroduce cross-lane collisions. */
const PORT_ENV = 'EVIDGATE_WEB_E2E_PORT';
const port = Number(process.argv[2] ?? process.env[PORT_ENV] ?? '');

const CONTENT_TYPES = new Map([
  ['.css', 'text/css; charset=utf-8'],
  ['.html', 'text/html; charset=utf-8'],
  ['.ico', 'image/x-icon'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.map', 'application/json; charset=utf-8'],
  ['.svg', 'image/svg+xml'],
  ['.txt', 'text/plain; charset=utf-8'],
  ['.woff2', 'font/woff2'],
]);

/** Fail at server start with an actionable message; never build, never serve a fake page. */
function fail(message) {
  console.error(`evidgate-web-e2e: ${message}`);
  process.exit(1);
}

if (!Number.isInteger(port) || port <= 0 || port > 65_535) {
  fail(
    `no port. playwright.config.ts sets ${PORT_ENV} for this process, so start the server ` +
      'through Playwright (`npx playwright test`) or pass a port as the first argument.',
  );
}

if (!existsSync(INDEX_FILE) || !statSync(INDEX_FILE).isFile()) {
  fail(
    `${INDEX_FILE} is missing, so there is no bundle to serve. The E2E suite runs against ` +
      'the production output: run `npm run build -w apps/web` from the repository root first.',
  );
}

function sendText(response, status, body) {
  response.writeHead(status, { 'content-type': 'text/plain; charset=utf-8' }).end(body);
}

function sendFile(response, file, status = 200) {
  response.writeHead(status, {
    'content-type': CONTENT_TYPES.get(extname(file).toLowerCase()) ?? 'application/octet-stream',
    // The bundle is content-hashed and the server is short-lived: no caching layer to fool.
    'cache-control': 'no-store',
  });
  createReadStream(file).pipe(response);
}

/** Resolve a decoded request path to a file inside ROOT, or `null` when it escapes. */
function resolveInsideRoot(decodedPath) {
  const segments = decodedPath.split('/');
  if (segments.includes('..')) {
    return null;
  }
  const relative = segments.filter((segment) => segment !== '' && segment !== '.').join('/');
  if (relative === '') {
    return INDEX_FILE;
  }
  const file = resolve(ROOT, relative);
  // Backstop for anything the segment check could miss (a decoded separator, a symlinked
  // spelling): every read must sit below ROOT.
  return file === ROOT || file.startsWith(ROOT + sep) ? file : null;
}

const server = createServer((request, response) => {
  const { pathname } = new URL(request.url ?? '/', 'http://127.0.0.1');
  // `decodeURIComponent` throws URIError on a malformed escape (a stray `%`, `%zz`); a throw
  // in the request listener would be an uncaught exception and kill the whole process
  // mid-run, so a bad path is answered instead.
  let decoded;
  try {
    decoded = decodeURIComponent(pathname);
  } catch {
    sendText(response, 400, 'Bad Request');
    return;
  }
  if (decoded.includes('\0')) {
    sendText(response, 400, 'Bad Request');
    return;
  }

  const file = resolveInsideRoot(decoded);
  if (file === null) {
    sendText(response, 403, 'Forbidden');
    return;
  }

  if (existsSync(file) && statSync(file).isDirectory()) {
    sendFile(response, INDEX_FILE);
    return;
  }
  if (existsSync(file) && statSync(file).isFile()) {
    sendFile(response, file);
    return;
  }

  // SPA fallback only for something that is not an asset request.
  const extension = extname(file).toLowerCase();
  if (!CONTENT_TYPES.has(extension)) {
    sendFile(response, INDEX_FILE);
    return;
  }
  sendText(response, 404, 'Not Found');
});

server.listen(port, '127.0.0.1', () => {
  console.log(`evidgate-web-e2e: serving ${ROOT} on http://127.0.0.1:${port}/`);
});

server.on('error', (error) => {
  // Most likely EADDRINUSE: the config picks a random high port, so report the port rather
  // than letting an uncaught exception produce a stack trace.
  fail(`cannot listen on 127.0.0.1:${port} (${error.code ?? error.message}).`);
});

// Playwright stops the command when the run ends; closing on the signal frees the port for
// the next run instead of leaving a process that `reuseExistingServer` would then reuse.
for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
