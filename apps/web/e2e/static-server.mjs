#!/usr/bin/env node
// 09.1.1-T1.2 (00 §5.6 W9, AC2): the static server the Playwright `webServer` in
// playwright.config.ts starts. It serves the PRODUCTION bundle built into
// `apps/web/dist/browser` — deliberately a plain Node http server instead of
// `ng serve` or a static-server package: the E2E gate must fail on a stale dist, must not
// depend on the dev server's live rebuild, and must add no dependency to apps/web.
//
// The behaviours that carry real logic have their own specs in `e2e/static-server.spec.ts`:
//   - SPA fallback: an extension-less client route such as `/runs/<id>` is answered with
//     `index.html` (200). Any final path segment carrying an extension is an asset request,
//     whether this file knows the type or not, and a missing asset is answered 404 — a broken
//     bundle reference can never come back as an HTML page with a 200.
//   - traversal and symlink guards: the path is decoded, `..` segments are rejected, the
//     resolved path must sit below the root, and the candidate is then resolved once more
//     through `realpathSync` so a symlink inside the bundle cannot lead outside it. Neither
//     the request path nor a link target can make the server read a file above `dist/browser`.
//   - one bad request never stops the server: a malformed request line, a malformed escape,
//     an unsupported method and a failed file read are answered (400/405/500) instead of
//     throwing out of the request listener, where nothing would catch them.
import { createReadStream, existsSync, realpathSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, resolve, sep } from 'node:path';
import { pipeline } from 'node:stream';
import { fileURLToPath } from 'node:url';

/**
 * The directory to serve. Playwright's `webServer` never sets `EVIDGATE_WEB_E2E_ROOT`, so the
 * gate always serves `../dist/browser`; only `e2e/static-server.spec.ts` sets it, pointing the
 * server at a temporary dist to exercise the failure modes (an unreadable file, a symlink out
 * of the bundle) that must never exist in a real build.
 */
const ROOT_ENV = 'EVIDGATE_WEB_E2E_ROOT';
const ROOT = resolve(
  process.env[ROOT_ENV] ?? fileURLToPath(new URL('../dist/browser/', import.meta.url)),
);
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

/** The root with every link resolved; the symlink guard compares against this, not `ROOT`. */
const REAL_ROOT = realpathSync(ROOT);

/**
 * `index.html` with its links resolved, and the only spelling ever served as the fallback: the
 * checks above follow a symlink, so without this a `dist/browser/index.html` linking outside
 * would hand out that file for `/` and for every client route.
 */
const REAL_INDEX = realInsideRoot(INDEX_FILE);
if (typeof REAL_INDEX !== 'string') {
  fail(
    `${INDEX_FILE} does not resolve to a file inside ${ROOT}. Serve a built bundle: the ` +
      'fallback page must be a real file of `dist/browser`, never a link that leaves it.',
  );
}

function sendText(response, status, body) {
  response.writeHead(status, { 'content-type': 'text/plain; charset=utf-8' }).end(body);
}

/**
 * Stream `file` to `response`. The headers are written only once the read stream is open, so a
 * file that cannot be read at all (EACCES, EISDIR, removed after the stat) is still answerable
 * with a 500. `pipeline`, never a bare `pipe` — a pipe has no error handler, so a read error
 * would be an uncaught exception that kills the process — then owns the transfer; once the
 * status line is on the wire a failed read can only end the response.
 */
function sendFile(response, file, status = 200) {
  const source = createReadStream(file);
  let started = false;
  source.once('open', () => {
    started = true;
    response.writeHead(status, {
      'content-type': CONTENT_TYPES.get(extname(file).toLowerCase()) ?? 'application/octet-stream',
      // The bundle is content-hashed and the server is short-lived: no caching layer to fool.
      'cache-control': 'no-store',
    });
    pipeline(source, response, (error) => {
      if (error) {
        response.destroy();
      }
    });
  });
  source.once('error', () => {
    // Before 'open' nothing has been written, and `pipeline` destroys its destination on a
    // failed read, which would turn this answerable 500 into a reset connection.
    if (!started) {
      sendText(response, 500, 'Internal Server Error');
    }
  });
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
  // Backstop for anything the segment check could miss (a decoded separator, a link spelled
  // like a file): every spelling must sit below ROOT before the filesystem is even asked.
  return file === ROOT || file.startsWith(ROOT + sep) ? file : null;
}

/** `statSync` that reports a missing or unreadable path as `undefined` instead of throwing. */
function statOrUndefined(file) {
  try {
    return statSync(file);
  } catch {
    return undefined;
  }
}

/**
 * `realpathSync(file)` when the resolved path stays inside the served root, `null` when it
 * escapes it (a link pointing above the root, a link loop), `undefined` when there is nothing
 * there. Only the real path is ever opened, so a symlink inside the bundle cannot hand out a
 * file outside it.
 */
function realInsideRoot(file) {
  let real;
  try {
    real = realpathSync(file);
  } catch (error) {
    // Nothing at that path: the caller decides between the fallback and the 404.
    return error?.code === 'ENOENT' || error?.code === 'ENOTDIR' ? undefined : null;
  }
  return real === REAL_ROOT || real.startsWith(REAL_ROOT + sep) ? real : null;
}

/** The only methods a static bundle answers; anything else is a client bug, not a read. */
const ALLOWED_METHODS = 'GET, HEAD';

const server = createServer((request, response) => {
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    response
      .writeHead(405, {
        allow: ALLOWED_METHODS,
        'content-type': 'text/plain; charset=utf-8',
      })
      .end('Method Not Allowed');
    return;
  }

  // Both the URL parse and the decode live inside this try because both throw on hostile
  // input: `new URL` on a request line such as `GET //[ HTTP/1.1` (the `//` makes the target
  // protocol-relative, so `[` is parsed as an invalid host) and `decodeURIComponent` on a
  // malformed escape (`%zz`, a stray `%`). A throw in the request listener is an uncaught
  // exception that kills the server mid-run, so the answer is a 400 instead.
  let decoded;
  try {
    decoded = decodeURIComponent(new URL(request.url ?? '/', 'http://127.0.0.1').pathname);
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

  const real = realInsideRoot(file);
  if (real === null) {
    sendText(response, 403, 'Forbidden');
    return;
  }

  const stats = real === undefined ? undefined : statOrUndefined(real);
  if (stats?.isDirectory()) {
    sendFile(response, REAL_INDEX);
    return;
  }
  if (stats?.isFile()) {
    sendFile(response, real);
    return;
  }

  // SPA fallback only for something that is not an asset request: any final path segment
  // carrying an extension (`.js`, `.css`, `.png`, `.woff2`, `.webmanifest`, `.wasm`, …) is an
  // asset, known to CONTENT_TYPES or not, so a missing asset is a 404.
  if (extname(file) !== '') {
    sendText(response, 404, 'Not Found');
    return;
  }
  sendFile(response, REAL_INDEX);
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
// the next run instead of leaving a process behind that would hold it.
for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
