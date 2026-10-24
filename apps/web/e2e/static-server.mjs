#!/usr/bin/env node
// 09.1.1-T1.2 (00 §5.6 W9, AC2): the static server the Playwright `webServer` in
// playwright.config.ts starts. It serves the PRODUCTION bundle built into
// `apps/web/dist/browser` — deliberately a plain Node http server instead of
// `ng serve` or a static-server package: the E2E gate must fail on a stale dist, must not
// depend on the dev server's live rebuild, and must add no dependency to apps/web.
//
// 09.1.1-T6.1: the bundle is localized, so `dist/browser` holds one directory per locale
// (`en/`, `vi/`), each with its own index.html and chunks, and no root index.html. The server
// therefore routes on the first path segment:
//   - `GET /` answers a 302 to `/<default-locale>/` (vi: the product's default language).
//   - `/en/…` and `/vi/…` resolve inside that locale's directory, and the extension-less SPA
//     fallback returns THAT locale's index.html.
//   - a first segment that is not a locale directory (the set is read from the dist at start)
//     is an ordinary SPA path: it resolves against the bundle root and falls back to the
//     DEFAULT locale's index.html, because the root deliberately holds no page of its own.
// A dist whose root itself holds index.html (a build without `localize`, and every temporary
// dist in `e2e/static-server.spec.ts`) is served as the single layout it is, with no redirect.
//
// The behaviours that carry real logic have their own specs in `e2e/static-server.spec.ts`:
//   - SPA fallback: an extension-less client route such as `/runs/<id>` is answered with
//     `index.html` (200). Any final path segment carrying an extension is an asset request,
//     whether this file knows the type or not, and a missing asset is answered 404 — a broken
//     bundle reference can never come back as an HTML page with a 200.
//   - traversal and symlink guards: the path is decoded, `..` segments are rejected, the
//     resolved path must sit below the served directory, and the candidate is then resolved
//     once more through `realpathSync` so a symlink inside the bundle cannot lead outside it.
//     Neither the request path nor a link target can make the server read a file above
//     `dist/browser`, and that includes the per-locale index.html files.
//   - one bad request never stops the server: a malformed request line, a malformed escape,
//     an unsupported method and a failed file read are answered (400/405/500) instead of
//     throwing out of the request listener, where nothing would catch them.
import { createReadStream, readdirSync, realpathSync, statSync } from 'node:fs';
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

/** The language `/` redirects to, when the bundle has it: Vietnamese is the default locale. */
const PREFERRED_LOCALE = 'vi';

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

/** `true` for a regular file; a missing path and a directory of that name both answer `false`. */
function isFile(path) {
  try {
    return statSync(path).isFile();
  } catch {
    return false;
  }
}

/**
 * The locale directories of the bundle: every direct subdirectory of `root` that holds an
 * index.html, sorted. `ng build` with `localize` writes exactly one per locale
 * (`dist/browser/en`, `dist/browser/vi`); a build without `localize` has none.
 */
function localeDirectories(root) {
  return readdirSync(root, { withFileTypes: true })
    .filter((entry) => entry.isDirectory() && isFile(join(root, entry.name, 'index.html')))
    .map((entry) => entry.name)
    .sort();
}

let LOCALES = [];
try {
  LOCALES = localeDirectories(ROOT);
} catch {
  // ROOT itself is missing; the check below reports it with the build command to run.
}

/**
 * No locale directory means the bundle is a single layout with index.html at the root. A dist
 * that has both is served as the locale layout: that is what `localize` emits, and a stale
 * root index.html must not shadow it.
 */
const ROOT_LAYOUT = LOCALES.length === 0;
if (ROOT_LAYOUT && !isFile(INDEX_FILE)) {
  fail(
    `${ROOT} holds no bundle to serve: no index.html and no <locale>/index.html. The E2E ` +
      'suite runs against the production output: run `npm run build -w apps/web` from the ' +
      'repository root first.',
  );
}

/** The locale `/` and an un-prefixed client route fall back to. */
const START_LOCALE = LOCALES.includes(PREFERRED_LOCALE) ? PREFERRED_LOCALE : LOCALES[0];

/** The root with every link resolved; the symlink guard compares against this, not `ROOT`. */
const REAL_ROOT = realpathSync(ROOT);

/**
 * Locale label ('' for the single-layout build) to the index.html that layout falls back to,
 * already resolved. Only a real path is ever served, so a link out of the bundle cannot become
 * the page every client route answers with.
 */
const FALLBACK_INDEX = new Map();

function registerFallback(label, indexFile) {
  const real = realInsideRoot(indexFile);
  if (typeof real !== 'string') {
    fail(
      `${indexFile} does not resolve to a file inside ${ROOT}. Serve a built bundle: the ` +
        'fallback page must be a real file of `dist/browser`, never a link that leaves it.',
    );
  }
  FALLBACK_INDEX.set(label, real);
}

if (ROOT_LAYOUT) {
  registerFallback('', INDEX_FILE);
} else {
  for (const locale of LOCALES) {
    registerFallback(locale, join(ROOT, locale, 'index.html'));
  }
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

/**
 * Where a decoded request path points: the directory it resolves against, its remaining
 * segments, the index.html this layout falls back to, and whether the path resolved against the
 * bundle root rather than a locale. `null` when the path escapes the served tree. In the locale
 * layout the first segment selects the locale directory; a first segment that is no locale is an
 * ordinary SPA path against the bundle root, which falls back to the default locale's page (the
 * root of a localized build has no page of its own).
 */
function resolveRequest(decodedPath) {
  const segments = decodedPath.split('/');
  if (segments.includes('..')) {
    return null;
  }
  const parts = segments.filter((segment) => segment !== '' && segment !== '.');
  if (ROOT_LAYOUT) {
    return { baseDir: ROOT, parts, fallback: FALLBACK_INDEX.get(''), fromRoot: true };
  }
  const [first, ...rest] = parts;
  if (first !== undefined && FALLBACK_INDEX.has(first)) {
    return {
      baseDir: join(ROOT, first),
      parts: rest,
      fallback: FALLBACK_INDEX.get(first),
      fromRoot: false,
    };
  }
  return { baseDir: ROOT, parts, fallback: FALLBACK_INDEX.get(START_LOCALE), fromRoot: true };
}

/** Resolve `parts` inside `baseDir` to a path, or `null` when it escapes `baseDir`. */
function resolveIn(baseDir, parts) {
  if (parts.length === 0) {
    return join(baseDir, 'index.html');
  }
  const file = resolve(baseDir, parts.join('/'));
  // Backstop for anything the segment check could miss (a decoded separator, a link spelled
  // like a file): every spelling must sit below `baseDir` before the filesystem is even asked.
  return file === baseDir || file.startsWith(baseDir + sep) ? file : null;
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

  const route = resolveRequest(decoded);
  if (route === null) {
    sendText(response, 403, 'Forbidden');
    return;
  }

  // Locale layout: the bundle root holds no page, so `/` redirects to the default locale.
  // 302, not 301 — the target is a build artefact, and a permanently cached redirect would
  // outlive the dist it was served from. A path that names a locale is never redirected, so
  // `/vi/` and `/vi` answer with that locale's page.
  if (!ROOT_LAYOUT && route.fromRoot && route.parts.length === 0) {
    response.writeHead(302, { location: `/${START_LOCALE}/` }).end();
    return;
  }

  const file = resolveIn(route.baseDir, route.parts);
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
    sendFile(response, route.fallback);
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
  sendFile(response, route.fallback);
});

server.listen(port, '127.0.0.1', () => {
  const layout = ROOT_LAYOUT ? 'single layout' : `locales ${LOCALES.join(', ')}`;
  console.log(`evidgate-web-e2e: serving ${ROOT} (${layout}) on http://127.0.0.1:${port}/`);
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
