// 01.6.1-T5.1 (AC7): the page the Playwright `webServer` in playwright.config.ts serves.
// Deliberately a plain Node http server: the smoke test must prove the runner, the browser
// install and the server wiring without pulling a static-server package into apps/web
// (09.1.1-T2 replaces this with the Angular dev server / built bundle).
import { createReadStream, existsSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, normalize, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(fileURLToPath(new URL('./public/', import.meta.url)));
const PORT = Number(process.argv[2] ?? process.env.PORT ?? 4173);

const CONTENT_TYPES = {
  '.css': 'text/css; charset=utf-8',
  '.html': 'text/html; charset=utf-8',
  '.ico': 'image/x-icon',
  '.js': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
};

const server = createServer((request, response) => {
  const { pathname } = new URL(request.url ?? '/', 'http://127.0.0.1');
  // `decodeURIComponent` throws URIError on a malformed escape (a stray `%`, `%zz`): a
  // throw in the request listener would be an uncaught exception and kill the whole
  // process mid-run, so a bad path is answered like any other unknown one.
  let decoded;
  try {
    decoded = decodeURIComponent(pathname);
  } catch {
    response.writeHead(400, { 'content-type': 'text/plain; charset=utf-8' }).end('Bad Request');
    return;
  }
  // `normalize` collapses `..` (encoded ones included, via decodeURIComponent), and the
  // prefix check keeps every read inside `public/` even for a path that escapes it.
  const relative = normalize(decoded).replace(/^\/+/, '');
  let file = resolve(ROOT, relative);
  if (file !== ROOT && !file.startsWith(ROOT + sep)) {
    response.writeHead(403, { 'content-type': 'text/plain; charset=utf-8' }).end('Forbidden');
    return;
  }
  if (existsSync(file) && statSync(file).isDirectory()) {
    file = join(file, 'index.html');
  }
  if (!existsSync(file)) {
    response.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' }).end('Not Found');
    return;
  }
  response.writeHead(200, {
    'content-type': CONTENT_TYPES[extname(file)] ?? 'application/octet-stream',
  });
  createReadStream(file).pipe(response);
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`evidgate-web-static: http://127.0.0.1:${PORT}/`);
});

// Playwright stops the command when the run ends; closing on the signal frees the port for
// the next run instead of leaving a process that `reuseExistingServer` would then reuse.
for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
