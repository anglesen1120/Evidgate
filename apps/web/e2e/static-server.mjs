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
// dist in `e2e/static-server*.spec.ts`) is served as the single layout it is, with no redirect.
//
// 09.1.1-T10 (AC7, ADR-0008 rule 4): every response this server writes carries its own
// `content-security-policy` header, built by `scripts/csp.mjs` out of
// `scripts/app-origin-csp.json` — the same policy the deployed edge sends, and the header set
// 10.2.1-T1.4 reuses, so the E2E gate exercises the production policy instead of one invented
// here. The policy forbids inline script and eval, requires Trusted Types for `script`, and
// allows `trusted-types angular` only: the app origin deliberately has no
// `angular#unsafe-bypass`, so `bypassSecurityTrust*` fails in the browser as well as in lint.
// Styles are the one nonce-based hole:
//   - a fresh nonce is generated for EVERY response (document, asset, redirect, error text) and
//     the status line plus that header are always written through `writeResponseHead`, so no
//     answer path can forget the policy;
//   - every answer is also sent `cache-control: no-store`, because the policy is a per-response
//     decision: a stored copy would replay a nonce that the next response's header does not
//     allow, whether it is a document, an asset, a redirect or an error text;
//   - HTML documents (each locale's index.html and the SPA fallback) are read whole instead of
//     streamed, because `__CSP_NONCE__` is replaced there — in `ngCspNonce` and in every other
//     occurrence — with the nonce of that response's own header;
//   - assets are still streamed untouched, with a policy header but no nonce in the body, where
//     it could not be used anyway.
// `EVIDGATE_WEB_CONNECT_SRC` widens `connect-src` beyond `'self'` for a suite that has to call
// another origin; it is validated once at start, so a wildcard or a path fails the server
// loudly instead of weakening every response.
//
// The behaviours that carry real logic have their own specs in `e2e/static-server*.spec.ts`:
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
import { createReadStream, readdirSync, readFileSync, realpathSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, resolve, sep } from 'node:path';
import { pipeline } from 'node:stream';
import { fileURLToPath } from 'node:url';

import {
  buildSecurityHeaders,
  generateNonce,
  injectNonce,
  parseConnectSrc,
} from '../scripts/csp.mjs';

/**
 * The directory to serve. Playwright's `webServer` never sets `EVIDGATE_WEB_E2E_ROOT`, so the
 * gate always serves `../dist/browser`; only `e2e/static-server*.spec.ts` sets it, pointing the
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

/** Extra origins for `connect-src`; parsed once, because the policy is rebuilt per response. */
const CONNECT_SRC_ENV = 'EVIDGATE_WEB_CONNECT_SRC';

function readConnectSrc() {
  try {
    return parseConnectSrc(process.env[CONNECT_SRC_ENV]);
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error);
    return fail(`invalid ${CONNECT_SRC_ENV}: ${reason}`);
  }
}

const CONNECT_SRC = readConnectSrc();

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

/**
 * The headers every response carries, for `nonce`. Built from the shared policy, so the E2E
 * gate and the deployed edge cannot drift apart, and with the app origin's `connect-src`
 * extras appended.
 */
function securityHeaders(nonce) {
  return buildSecurityHeaders({ nonce, connectSrc: CONNECT_SRC });
}

/**
 * Write the status line and the headers of one response, always with a policy of its own. The
 * nonce is generated here — once per response — and returned, so the document that is about to
 * be injected can use the very nonce this response's header authorises. Every writeHead in this
 * file goes through this function: that is what keeps a forgotten header from becoming a served
 * page with no policy at all.
 */
function writeResponseHead(response, status, headers, nonce = generateNonce()) {
  response.writeHead(status, { ...securityHeaders(nonce), ...headers });
  return nonce;
}

/**
 * Answer with plain text (an error text). `cache-control: no-store` applies to every answer this
 * server writes, not only to documents: a stored error text would be replayed without its policy
 * ever being re-evaluated, and the policy of a response is a per-response decision.
 */
function sendText(response, status, body) {
  writeResponseHead(response, status, {
    'content-type': 'text/plain; charset=utf-8',
    'cache-control': 'no-store',
  });
  response.end(body);
}

/**
 * Answer with an HTML document. Read whole rather than streamed because `__CSP_NONCE__` has to
 * be replaced before the body goes out, which means the length must be known in advance. The
 * nonce written into the body is the one in this response's header, and nothing else: Angular
 * reads it from `ngCspNonce` at runtime to authorise the styles it writes itself, which is the
 * only reason `style-src` may carry a nonce at all.
 *
 * `cache-control: no-store` is part of the contract: a stored copy would replay a nonce that the
 * next response's header does not allow, and a HEAD answer carries the same headers (so the same
 * length) with no body.
 */
function sendDocument(response, file, status = 200, includeBody = true) {
  let html;
  try {
    html = readFileSync(file, 'utf8');
  } catch {
    // Nothing has been written yet: the request is answerable with a 500 that still carries its
    // own policy header (sendText writes it). A failed read must never kill the process.
    sendText(response, 500, 'Internal Server Error');
    return;
  }
  const nonce = generateNonce();
  const body = injectNonce(html, nonce);
  writeResponseHead(
    response,
    status,
    {
      'content-type': CONTENT_TYPES.get('.html'),
      'content-length': String(Buffer.byteLength(body)),
      'cache-control': 'no-store',
    },
    nonce,
  );
  response.end(includeBody ? body : undefined);
}

/**
 * Stream `file` to `response`. The headers are written only once the read stream is open, so a
 * file that cannot be read at all (EACCES, EISDIR, removed after the stat) is still answerable
 * with a 500. `pipeline`, never a bare `pipe` — a pipe has no error handler, so a read error
 * would be an uncaught exception that kills the process — then owns the transfer; once the
 * status line is on the wire a failed read can only end the response.
 *
 * An HTML document goes to `sendDocument` instead: its body has to be rewritten, so it cannot be
 * streamed. Everything else is streamed untouched, with a policy header whose fresh nonce never
 * appears in the body.
 */
function sendFile(response, file, status = 200, includeBody = true) {
  const type = extname(file).toLowerCase();
  if (type === '.html') {
    sendDocument(response, file, status, includeBody);
    return;
  }
  const source = createReadStream(file);
  let started = false;
  source.once('open', () => {
    // A client that hung up while the file was opening has already destroyed `response`;
    // piping into it raises ERR_STREAM_UNABLE_TO_PIPE as an uncaught exception that would
    // stop the server for the rest of the run, so release the descriptor and stop instead.
    if (response.destroyed || response.writableEnded) {
      source.destroy();
      return;
    }
    started = true;
    writeResponseHead(response, status, {
      'content-type': CONTENT_TYPES.get(type) ?? 'application/octet-stream',
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
  // A HEAD answer sends this response's headers, including the policy and therefore the
  // document length, and no body at all.
  const includeBody = request.method !== 'HEAD';

  if (request.method !== 'GET' && request.method !== 'HEAD') {
    writeResponseHead(response, 405, {
      allow: ALLOWED_METHODS,
      'content-type': 'text/plain; charset=utf-8',
      'cache-control': 'no-store',
    });
    response.end('Method Not Allowed');
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
    writeResponseHead(response, 302, {
      location: `/${START_LOCALE}/`,
      'cache-control': 'no-store',
    });
    response.end();
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
    sendFile(response, route.fallback, 200, includeBody);
    return;
  }
  if (stats?.isFile()) {
    sendFile(response, real, 200, includeBody);
    return;
  }

  // SPA fallback only for something that is not an asset request: any final path segment
  // carrying an extension (`.js`, `.css`, `.png`, `.woff2`, `.webmanifest`, `.wasm`, …) is an
  // asset, known to CONTENT_TYPES or not, so a missing asset is a 404.
  if (extname(file) !== '') {
    sendText(response, 404, 'Not Found');
    return;
  }
  sendFile(response, route.fallback, 200, includeBody);
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
