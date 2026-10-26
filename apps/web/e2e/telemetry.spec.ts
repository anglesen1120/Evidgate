import { expect, test, type Page } from '@playwright/test';
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { createServer } from 'node:net';
import { join } from 'node:path';

// 09.1.1-T9.2 (plan 09.1.1 §8 privacy row, §10 T9.2; AC "vitals event observed in Playwright
// smoke; opt-out honoured"): what the PRODUCTION bundle really puts on the wire.
//
// `apps/web` is absent from the codebase-memory index, so the conventions of
// `e2e/smoke.spec.ts`, `e2e/static-server.spec.ts`, `e2e/csp.spec.ts` and `playwright.config.ts`
// were read directly. Nothing of `src/` is imported: the attribute allowlist and the span names
// are copied literally into this file, because a spec that imports the gate it observes can only
// prove that the gate is self-consistent.
//
// Every case runs against its OWN `e2e/static-server.mjs` over the real `dist/browser`, so a case
// decides the `connect-src` extension for itself (D-S99-Q3) without touching
// `playwright.config.ts` or `scripts/app-origin-csp.json`: the committed policy keeps
// `connect-src 'self'`, and the collector origin is 10.3.1-T2's [OPEN].
//
// Nothing here waits on a clock. The two asynchronous facts of the app — "the telemetry chunk has
// started" and "a span left the page" — are observed as network events (the collector POST, the
// CSP violation) and retried by `expect.poll`, so a slow CI host lengthens the wait instead of
// breaking it, and a case that expects NOTHING still runs against a control page from the same
// build that exports.

const SERVER_FILE = join(__dirname, 'static-server.mjs');
const COLLECTOR_ORIGIN = 'https://otlp.e2e.evidgate.test';
const ENDPOINT = `${COLLECTOR_ORIGIN}/v1/traces`;
const SERVICE_VERSION = '1.2.3';
const ROOT_ENV = 'EVIDGATE_WEB_E2E_ROOT';
const CONNECT_SRC_ENV = 'EVIDGATE_WEB_CONNECT_SRC';

/** `ALLOWED_ATTRIBUTE_KEYS` of `src/app/core/telemetry/attribute-allowlist.ts`, verbatim. */
const ATTRIBUTE_KEYS: Record<string, true> = {
  'http.request.method': true,
  'http.response.status_code': true,
  'http.route': true,
  'evidgate.route': true,
  'evidgate.outcome': true,
  'evidgate.request_id': true,
  'evidgate.locale': true,
  'evidgate.api.error_code': true,
  'evidgate.api.error_kind': true,
  'evidgate.api.retryable': true,
  'evidgate.error.source': true,
  'error.type': true,
  'web_vital.name': true,
  'web_vital.value': true,
  'web_vital.rating': true,
  'web_vital.navigation_type': true,
};

/** `SPAN_NAMES` of `src/app/core/telemetry/telemetry-names.ts`, verbatim. */
const SPAN_NAMES: Record<string, true> = {
  'route.change': true,
  'api.request': true,
  'client.error': true,
  web_vital: true,
  'client.event': true,
};

/** The W3C traceparent T3 writes when telemetry leaves a request's header bare. */
const TRACEPARENT = /^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$/;
const UUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;

interface OtlpAttribute {
  readonly key: string;
  readonly value: Record<string, unknown>;
}

interface OtlpSpan {
  readonly name: string;
  readonly attributes?: readonly OtlpAttribute[];
  readonly events?: readonly { readonly attributes?: readonly OtlpAttribute[] }[];
}

interface OtlpResourceSpans {
  readonly resource: { readonly attributes: readonly OtlpAttribute[] };
  readonly scopeSpans: readonly { readonly spans: readonly OtlpSpan[] }[];
}

/** The OTLP/HTTP JSON body the browser exporter POSTs. */
interface OtlpExport {
  readonly resourceSpans: readonly OtlpResourceSpans[];
}

interface Server {
  readonly url: string;
  readonly child: ChildProcessWithoutNullStreams;
}

/** Everything one page told us about itself, captured from its own events only. */
interface Observation {
  /** Methods of every request the page addressed to the collector origin. */
  readonly requests: string[];
  /** `traceparent` of every same-origin `/v1` request, in order (may be `undefined`). */
  readonly apiTraceparents: (string | undefined)[];
  /** Paths of every same-origin script the page fetched, so "the chunk never loaded" is provable. */
  readonly scripts: string[];
  /** Uncaught page errors; a telemetry failure must never surface here. */
  readonly errors: string[];
  /** Decoded bodies of the intercepted collector POSTs, in arrival order. */
  readonly exports: OtlpExport[];
  /** How often the route handler was entered, including requests no listener could see. */
  routed: number;
}

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
    throw new Error('could not reserve a port for the telemetry static server');
  }
  return port;
}

/**
 * Start `e2e/static-server.mjs` over the real `dist/browser`. `connectSrc` decides the policy
 * this server sends: the collector origin, or nothing at all (the committed `connect-src 'self'`).
 */
async function startServer(connectSrc: string | null): Promise<Server> {
  const port = await freePort();
  const env = { ...process.env };
  // The suite's own `webServer` exports both variables to its children; a server whose policy is
  // the subject of the case must not inherit them.
  delete env[ROOT_ENV];
  delete env[CONNECT_SRC_ENV];
  if (connectSrc !== null) {
    env[CONNECT_SRC_ENV] = connectSrc;
  }
  const child = spawn(process.execPath, [SERVER_FILE, String(port)], { env });
  try {
    await new Promise<void>((resolveServing, rejectServing) => {
      let stderr = '';
      const timer = setTimeout(() => {
        rejectServing(new Error(`static-server.mjs did not serve within 10 s: ${stderr}`));
      }, 10_000);
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
  } catch (error) {
    child.kill('SIGKILL');
    throw error;
  }
  return { url: `http://127.0.0.1:${port}`, child };
}

/** Never leave a spawned server behind, whatever the case did. */
async function stopServer(server: Server): Promise<void> {
  const { child } = server;
  if (child.exitCode !== null || child.signalCode !== null) {
    return;
  }
  const exited = new Promise<void>((resolveExit) => child.once('exit', () => resolveExit()));
  child.kill('SIGTERM');
  // The server closes its listener on SIGTERM; a socket a client never ended must not hang the
  // suite, so the process is killed outright after a grace period. SIGKILL always ends the wait.
  const escalate = setTimeout(() => child.kill('SIGKILL'), 2_000);
  await exited;
  clearTimeout(escalate);
}

/**
 * Wire one page: record what it asks for, intercept the collector, and — when `injectMeta` is
 * set — add the deploy `<meta>` tags to the served document.
 *
 * The document is fetched through `route.fetch()` and re-served with its ORIGINAL headers, so the
 * response keeps its own `content-security-policy` and the nonce that policy allows; only the head
 * gains the tags, before the first script, the way the hosting edge injects them. A meta tag needs
 * no nonce: `script-src`/`style-src` are untouched.
 */
async function instrument(page: Page, origin: string, injectMeta: boolean): Promise<Observation> {
  const observation: Observation = {
    requests: [],
    apiTraceparents: [],
    scripts: [],
    errors: [],
    exports: [],
    routed: 0,
  };
  page.on('pageerror', (error) => observation.errors.push(error.message));
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (url.origin === COLLECTOR_ORIGIN) {
      observation.requests.push(request.method());
      return;
    }
    if (url.origin !== origin) {
      return;
    }
    if (request.resourceType() === 'script') {
      observation.scripts.push(url.pathname);
    }
    if (url.pathname === '/v1' || url.pathname.startsWith('/v1/')) {
      observation.apiTraceparents.push(request.headers()['traceparent']);
    }
  });

  await page.route(
    (url: URL) => url.origin === COLLECTOR_ORIGIN,
    async (route) => {
      observation.routed += 1;
      const request = route.request();
      if (request.method() === 'POST') {
        observation.exports.push(JSON.parse(request.postData() ?? '') as OtlpExport);
      }
      // Answers the preflight too: the exporter POSTs `application/json` cross-origin, and the
      // collector must allow both the origin and that header (README "CORS pairing").
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        headers: {
          'access-control-allow-origin': origin,
          'access-control-allow-methods': 'POST, OPTIONS',
          'access-control-allow-headers': 'content-type',
        },
        body: '{}',
      });
    },
  );

  if (injectMeta) {
    await page.route(
      (url: URL) => url.origin === origin,
      async (route) => {
        if (route.request().resourceType() !== 'document') {
          await route.continue();
          return;
        }
        const response = await route.fetch();
        const html = await response.text();
        const tags =
          `<meta name="evidgate-otlp-endpoint" content="${ENDPOINT}">` +
          `<meta name="evidgate-service-version" content="${SERVICE_VERSION}">`;
        expect(html).toMatch(/<head(?:\s[^>]*)?>/i);
        const body = html.replace(/<head(\s[^>]*)?>/i, (head) => `${head}${tags}`);
        await route.fulfill({
          response,
          // Every original header — the policy and its nonce above all — survives; only the
          // length changes with the injected head.
          headers: {
            ...response.headers(),
            'content-length': String(Buffer.byteLength(body)),
          },
          body,
        });
      },
    );
  }

  return observation;
}

/** Navigate to the English bundle and wait until the shell really rendered (not just served). */
async function boot(page: Page, origin: string): Promise<string> {
  const response = await page.goto(`${origin}/en/`);
  expect(response?.status()).toBe(200);
  await expect(page.locator('app-root app-shell')).toBeVisible();
  await expect(page.locator('app-root h1').first()).toBeVisible();
  // Two frames: an LCP entry is delivered after the frame that painted it, and the first synthetic
  // hide would otherwise set web-vitals' `firstHiddenTime` and drop the candidate ("Only report if
  // the page wasn't hidden prior to LCP"). Frames are the browser's own clock, not a fixed sleep.
  await page.evaluate(
    () =>
      new Promise<void>((resolveFrames) => {
        requestAnimationFrame(() => requestAnimationFrame(() => resolveFrames()));
      }),
  );
  return response?.headers()['content-security-policy'] ?? '';
}

/** The `connect-src` directive of a policy header, or '' when it has none. */
function connectSrc(policy: string): string {
  for (const part of policy.split(';')) {
    const trimmed = part.trim();
    if (trimmed === 'connect-src' || trimmed.startsWith('connect-src ')) {
      return trimmed;
    }
  }
  return '';
}

/**
 * Put the page in the hidden state the vitals flush is defined on (`telemetry.service.ts`
 * `onVisibilityChange`/`onPageHide`). The visibility fields are redefined because
 * `document.visibilityState` is a read-only getter; both events are dispatched because the facade
 * flushes on either, and `pagehide` is the one that also follows web-vitals' LCP finalisation.
 */
async function hide(page: Page): Promise<void> {
  await page.evaluate(() => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' });
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
    window.dispatchEvent(new Event('visibilitychange'));
    window.dispatchEvent(new Event('pagehide'));
  });
}

/**
 * Flush the page until `arrived` reports the export. The telemetry chunk starts after boot, so a
 * first flush may find no runtime; repeating the SAME page-leave event is what makes the wait
 * independent of how long the chunk took — no clock and no fixed sleep.
 */
async function flushUntil(page: Page, arrived: () => boolean, what: string): Promise<void> {
  await expect
    .poll(
      async () => {
        await hide(page);
        return arrived();
      },
      { timeout: 30_000, message: `waiting for ${what}` },
    )
    .toBe(true);
}

/** The spans of every intercepted export, flattened. */
function spansOf(exports: readonly OtlpExport[]): OtlpSpan[] {
  return exports.flatMap((body) =>
    body.resourceSpans.flatMap((resource) => resource.scopeSpans.flatMap((scope) => scope.spans)),
  );
}

/**
 * A trusted click: `web-vitals` finalises LCP only on an `isTrusted` `keydown`/`click` (see
 * `node_modules/web-vitals/dist/modules/onLCP.js`), and a synthetic event would silently leave the
 * metric open. The click goes through the real input pipeline at the heading's own coordinates, so
 * no overlay can fail an actionability check; nothing in the shell reacts to a heading click.
 */
async function clickHeading(page: Page): Promise<void> {
  const box = await page.locator('app-root h1').first().boundingBox();
  expect(box).not.toBeNull();
  if (box !== null) {
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  }
}

/**
 * Every span of every export must be scrubbed: a known name, allowlisted attribute and event keys
 * only, a value carrying no query string, fragment or URL, and no UUID outside
 * `evidgate.request_id` — the one id the allowlist admits.
 */
function expectScrubbedExports(exports: readonly OtlpExport[]): void {
  const resources = exports.flatMap((body) => body.resourceSpans);
  expect(resources.length).toBeGreaterThan(0);
  for (const resource of resources) {
    const resourceAttributes = resource.resource.attributes;
    const serviceName = resourceAttributes.find((attribute) => attribute.key === 'service.name');
    expect(serviceName?.value).toEqual({ stringValue: 'evidgate-web' });
    const version = resourceAttributes.find((attribute) => attribute.key === 'service.version');
    expect(version?.value).toEqual({ stringValue: SERVICE_VERSION });
  }
  for (const span of spansOf(exports)) {
    expect(SPAN_NAMES[span.name], `span name ${span.name}`).toBe(true);
    const attributes = [
      ...(span.attributes ?? []),
      ...(span.events ?? []).flatMap((event) => event.attributes ?? []),
    ];
    for (const attribute of attributes) {
      expect(ATTRIBUTE_KEYS[attribute.key], `attribute key ${attribute.key}`).toBe(true);
      const value = JSON.stringify(attribute.value);
      expect(value, `${attribute.key} carries a query, a fragment or a URL`).not.toMatch(
        /\?|#|https?:\/\//i,
      );
      if (attribute.key !== 'evidgate.request_id') {
        expect(value, `${attribute.key} carries a UUID`).not.toMatch(UUID);
      }
    }
  }
}

const servers: Server[] = [];
let allowedUrl = '';
let deniedUrl = '';

test.beforeAll(async () => {
  const allowed = await startServer(COLLECTOR_ORIGIN);
  servers.push(allowed);
  allowedUrl = allowed.url;
  const denied = await startServer(null);
  servers.push(denied);
  deniedUrl = denied.url;
});

test.afterAll(async () => {
  await Promise.all(servers.map((server) => stopServer(server)));
});

test.describe.configure({ mode: 'serial' });

test.describe('telemetry emission', () => {
  test('exports scrubbed spans with the configured resource', async ({ page }) => {
    const seen = await instrument(page, allowedUrl, true);
    const policy = await boot(page, allowedUrl);

    // D-S99-Q3: the server under test pairs the endpoint with exactly one `connect-src` origin, so
    // no wildcard, no bare scheme and no second origin can hide in the directive.
    expect(connectSrc(policy)).toBe(`connect-src 'self' ${COLLECTOR_ORIGIN}`);

    // A trusted click here, while the page is still visible, keeps the LCP value real: the browser
    // stops emitting entries on the first input, and `web-vitals` observes the buffered entry. The
    // metric is not reported yet, only finalised — see the click after the route span below.
    await clickHeading(page);

    // The landing navigation is replayed by the facade, so its span exists as soon as the chunk
    // started — which is exactly what the first intercepted POST proves.
    await flushUntil(
      page,
      () => spansOf(seen.exports).some((span) => span.name === 'route.change'),
      'the route.change span',
    );

    // The reporters exist now, so a trusted click finalises LCP and the next page-leave flush
    // emits it: the vitals path of the AC "vitals event observed in Playwright smoke".
    await clickHeading(page);
    await flushUntil(
      page,
      () => spansOf(seen.exports).some((span) => span.name === 'web_vital'),
      'the web_vital span',
    );

    expectScrubbedExports(seen.exports);
    expect(seen.errors).toEqual([]);
  });

  test('pairs with an unextended CSP by refusing the export, not the page', async ({ page }) => {
    await page.addInitScript(() => {
      document.addEventListener('securitypolicyviolation', (event) => {
        if (event.effectiveDirective === 'connect-src') {
          document.documentElement.dataset['collectorBlocked'] = 'true';
        }
      });
    });
    const seen = await instrument(page, deniedUrl, true);
    const policy = await boot(page, deniedUrl);

    // The committed policy, unextended: the collector origin is not a source of any kind.
    expect(connectSrc(policy)).toBe("connect-src 'self'");

    // The exporter is configured, so the flush is attempted; the browser refuses it before any
    // request exists. The violation — not a page error — is the only trace, and the page is still
    // rendered (boot asserted that).
    await expect
      .poll(
        async () => {
          await hide(page);
          return page.evaluate(
            () => document.documentElement.dataset['collectorBlocked'] === 'true',
          );
        },
        { timeout: 30_000, message: 'waiting for the blocked export' },
      )
      .toBe(true);

    expect(seen.requests).toEqual([]);
    expect(seen.routed).toBe(0);
    expect(seen.exports).toEqual([]);
    expect(seen.errors).toEqual([]);
  });
});

test.describe('telemetry opt-out', () => {
  // D-S99-Q1: one signal per case, each read once at start. The signals are read from `navigator`
  // before the chunk is requested, so a case shows BOTH that nothing reached the collector and
  // that a control page of the same build would have exported — the zero is only meaningful next
  // to that export.
  const signals: readonly { readonly name: string; readonly value: string | boolean }[] = [
    { name: 'doNotTrack', value: '1' },
    { name: 'msDoNotTrack', value: '1' },
    { name: 'globalPrivacyControl', value: true },
  ];

  for (const signal of signals) {
    test(`sends nothing to the collector when ${signal.name} asks not to be tracked`, async ({
      page,
      browser,
    }) => {
      await page.addInitScript(
        ({ name, value }: { readonly name: string; readonly value: string | boolean }) => {
          Object.defineProperty(navigator, name, { configurable: true, value });
        },
        { name: signal.name, value: signal.value },
      );
      const seen = await instrument(page, allowedUrl, true);
      await boot(page, allowedUrl);

      const control = await browser.newContext();
      try {
        const controlPage = await control.newPage();
        const controlSeen = await instrument(controlPage, allowedUrl, true);
        await boot(controlPage, allowedUrl);

        // The same page-leave flush that exports for the control must export nothing here; the
        // retry ends when the control's export arrives, i.e. once this page has had the same
        // build and the same number of flushes.
        await expect
          .poll(
            async () => {
              await Promise.all([hide(page), hide(controlPage)]);
              return spansOf(controlSeen.exports).length;
            },
            { timeout: 30_000, message: 'the control page must export without a privacy signal' },
          )
          .toBeGreaterThan(0);

        expect(seen.requests).toEqual([]);
        expect(seen.routed).toBe(0);
        expect(seen.exports).toEqual([]);
        // Opt-out is not a header scrub: T3 still stamps its own random traceparent on a
        // same-origin `/v1` request (request-id.interceptor.ts; README "traceparent"), and
        // telemetry never sees that request. What must not exist is a telemetry trace on the wire,
        // which the chunk proof below settles: the page never loaded the chunk at all.
        for (const traceparent of seen.apiTraceparents) {
          if (traceparent !== undefined) {
            expect(traceparent).toMatch(TRACEPARENT);
          }
        }
        // Every script this page loaded is also loaded by the control, and the control loads
        // strictly more — the telemetry chunk. The lazy chunk is fetched only after `start()`,
        // which returns before the import when the visit opted out.
        expect(seen.scripts.every((script) => controlSeen.scripts.includes(script))).toBe(true);
        expect(controlSeen.scripts.length).toBeGreaterThan(seen.scripts.length);
        expect(seen.errors).toEqual([]);
      } finally {
        await control.close();
      }
    });
  }

  test('contacts no collector when the endpoint meta tag is absent', async ({ page, browser }) => {
    const seen = await instrument(page, allowedUrl, false);
    await boot(page, allowedUrl);

    const control = await browser.newContext();
    try {
      const controlPage = await control.newPage();
      const controlSeen = await instrument(controlPage, allowedUrl, true);
      await boot(controlPage, allowedUrl);
      await expect
        .poll(
          async () => {
            await Promise.all([hide(page), hide(controlPage)]);
            return spansOf(controlSeen.exports).length;
          },
          { timeout: 30_000, message: 'the control page must export with an endpoint set' },
        )
        .toBeGreaterThan(0);

      // With no endpoint the exporter is the no-op one: the app still traces locally, and the
      // control proves the harness would have seen a POST had the tag been there.
      expect(seen.requests).toEqual([]);
      expect(seen.routed).toBe(0);
      expect(seen.exports).toEqual([]);
      expect(seen.errors).toEqual([]);
    } finally {
      await control.close();
    }
  });
});
