import { expect, test, type Response as PageResponse, type Route } from '@playwright/test';

// 09.1.1-T10 (AC7, ADR-0008 rule 4; T10.2): the app-origin CSP plus Trusted Types, asserted
// against what the static server REALLY sends. A unit test of `scripts/csp.mjs` would still
// pass while the server shipped a weakened policy, an unnonced page or an asset answer without
// a nonce, so the policy is re-parsed out of the response headers here and the browser is asked
// to enforce it.
//
// Two properties shape this file:
//   - the nonce is per RESPONSE, never per build or per process. Every helper therefore takes
//     the headers of the response it talks about; nothing caches a nonce across requests, and
//     two requests for the same locale are expected to disagree.
//   - the nonce is read through the IDL property (`element.nonce`). The `nonce` content
//     attribute is deliberately hidden from `getAttribute`, which returns '' by design, so an
//     `getAttribute('nonce')` assertion would read '' and prove nothing.
//
// `apps/web` is not in the codebase-memory graph (`check_index_coverage` reports it missing),
// so these conventions were read straight from `e2e/smoke.spec.ts`, `e2e/static-server.spec.ts`
// and `playwright.config.ts`. `scripts/csp.mjs` is not imported on purpose: it is plain ESM
// outside every tsconfig project, and the point of this suite is to observe the server, not to
// trust the builder that fed it.

const NONCE_PLACEHOLDER = '__CSP_NONCE__';
/** 16 bytes of `randomBytes` in standard base64: 22 characters plus `==`. */
const NONCE_PATTERN = /^[A-Za-z0-9+/]{22}==$/;
/** A complete CSP source token for that nonce, as it appears inside a directive. */
const NONCE_SOURCE_PATTERN = /^'nonce-[A-Za-z0-9+/]{22}=='$/;
const DEEP_RUN_ID = '2f1c9d3e-0b7a-4c55-9a12-6d0f4b8e1a77';

/** Colours the probe document asks for; a blocked declaration leaves the initial value. */
const UNNONCED_STYLE_BACKGROUND = 'rgb(4, 5, 6)';
const NONCED_STYLE_BACKGROUND = 'rgb(7, 8, 9)';
const STYLE_ATTRIBUTE_COLOR = 'rgb(1, 2, 3)';

interface LocaleTarget {
  /** Used in the test title, so a failure names the document that served it. */
  readonly label: string;
  readonly path: string;
}

// The locale root and one deep client route per locale: the deep route is answered by the SPA
// fallback with THAT locale's index.html, which is where a per-locale header mistake shows up.
const LOCALE_TARGETS: readonly LocaleTarget[] = [
  { label: 'vi locale root', path: '/vi/' },
  { label: 'en locale root', path: '/en/' },
  { label: 'vi deep client route', path: `/vi/runs/${DEEP_RUN_ID}` },
  { label: 'en deep client route', path: `/en/runs/${DEEP_RUN_ID}` },
];

interface ViolationRecord {
  readonly directive: string;
  readonly blockedURI: string;
  readonly sample: string;
}

/** The globals the init script installs; every read narrows `window` to this shape. */
interface ProbeWindow {
  __violations?: ViolationRecord[];
  __policyNames?: string[];
  __inlineScriptRan?: boolean | string;
  __pwned?: number;
}

interface ProbeState {
  readonly violations: readonly ViolationRecord[];
  readonly policyNames: readonly string[];
  readonly inlineScriptRan: boolean | string | null;
  readonly pwned: number | null;
}

interface PageProbeState {
  readonly styleNonces: readonly string[];
  readonly rootNonce: string | null;
}

interface ProbeStyles {
  readonly bodyBackground: string;
  readonly attributeColor: string;
  readonly noncedBackground: string;
}

/** One `createPolicy` attempt: the error name and message, both '' when nothing was thrown. */
interface PolicyAttempt {
  readonly error: string;
  readonly message: string;
}

interface TrustedTypesProbes {
  /** Error name per probe; '' means the call was NOT blocked, which fails the assertion. */
  readonly innerHtml: string;
  readonly scriptUrl: string;
  readonly scriptText: string;
  readonly evilPolicy: string;
  readonly unsafeBypassPolicy: string;
  /** The allow-listed name: '' error when it was still free, otherwise the refusal's detail. */
  readonly angularPolicy: PolicyAttempt;
  /** The second attempt's error name: 'TypeError' when the first one created the policy. */
  readonly angularPolicyDuplicate: string;
  readonly pwned: number | null;
  readonly injectedImage: boolean;
  readonly policyNames: readonly string[];
}

interface ReferencedAssets {
  readonly scripts: readonly string[];
  readonly styles: readonly string[];
}

/** What the policy-governed probe script reports; error names, '' when nothing was thrown. */
interface EvalProbeResult {
  readonly evalCall: string;
  readonly functionConstructor: string;
}

interface EvalProbeWindow {
  __evalProbe?: EvalProbeResult;
}

// TypeScript 6 removed every Trusted Types declaration from `lib.dom.d.ts` (no
// `trustedTypes`, `TrustedTypePolicyFactory`, `TrustedHTML`, …), and the workspace installs no
// `trusted-types` type package, so the three shapes this spec touches are declared here. Only
// `createPolicy` is modelled: that is the whole surface a page can use to obtain a policy, and
// the sinks themselves are typed by `lib.dom` already.
interface TrustedTypePolicyOptions {
  createHTML?: (input: string) => string;
  createScript?: (input: string) => string;
  createScriptURL?: (input: string) => string;
}

interface TrustedTypePolicy {
  readonly name: string;
  createHTML(input: string): string;
  createScript(input: string): string;
  createScriptURL(input: string): string;
}

/** The global Trusted Types factory; absent in a browser without the implementation. */
interface TrustedTypePolicyFactoryLike {
  createPolicy(policyName: string, policyOptions?: TrustedTypePolicyOptions): TrustedTypePolicy;
}

interface TrustedTypesGlobal {
  readonly trustedTypes?: TrustedTypePolicyFactoryLike;
}

/** Both `Response` and `APIResponse` expose their headers this way. */
interface HeaderSource {
  headers(): Record<string, string>;
}

/**
 * Installed before any page script through `page.addInitScript`: it records every CSP
 * violation the document reports and every Trusted Types policy the page manages to create.
 * `securitypolicyviolation` does not bubble, so the listener is registered in the capture
 * phase on `document`, where Chromium dispatches document-level violations.
 */
function installProbe(): void {
  const probe = window as unknown as ProbeWindow;
  probe.__violations = [];
  probe.__policyNames = [];
  document.addEventListener(
    'securitypolicyviolation',
    (event) => {
      probe.__violations?.push({
        directive: event.effectiveDirective,
        blockedURI: event.blockedURI,
        sample: event.sample,
      });
    },
    true,
  );

  // Trusted Types is a Chromium feature and may be absent; the assertions guard for that
  // rather than making the init script throw in a browser that has no implementation. The cast
  // restores the shapes `lib.dom` no longer ships (TS 6 dropped them) on the well-known global
  // itself; no value from outside the page crosses this line.
  const globals = window as unknown as TrustedTypesGlobal;
  const factory = globals.trustedTypes;
  if (factory !== undefined) {
    const originalCreatePolicy = factory.createPolicy.bind(factory);
    const recordingCreatePolicy: typeof factory.createPolicy = (name, options) => {
      const policy = originalCreatePolicy(name, options);
      // Only a policy the browser ACCEPTED is recorded: a refused name (one `trusted-types`
      // does not allow) throws out of here and must not appear in the list.
      probe.__policyNames?.push(name);
      return policy;
    };
    factory.createPolicy = recordingCreatePolicy;
  }
}

/** Reads the init script's globals of the current document. */
function readProbeState(): ProbeState {
  const probe = window as unknown as ProbeWindow;
  return {
    violations: probe.__violations ?? [],
    policyNames: probe.__policyNames ?? [],
    inlineScriptRan: probe.__inlineScriptRan ?? null,
    pwned: probe.__pwned ?? null,
  };
}

function readPageProbeState(): PageProbeState {
  return {
    styleNonces: Array.from(document.querySelectorAll('style'), (element) => element.nonce),
    rootNonce: document.querySelector('app-root')?.getAttribute('ngCspNonce') ?? null,
  };
}

function readProbeStyles(): ProbeStyles {
  const backgroundOf = (selector: string): string => {
    const element = document.querySelector(selector);
    if (element === null) {
      throw new Error(`the probe document is missing ${selector}`);
    }
    return getComputedStyle(element).backgroundColor;
  };
  const colorOf = (selector: string): string => {
    const element = document.querySelector(selector);
    if (element === null) {
      throw new Error(`the probe document is missing ${selector}`);
    }
    return getComputedStyle(element).color;
  };
  return {
    bodyBackground: backgroundOf('body'),
    attributeColor: colorOf('#attr-probe'),
    noncedBackground: backgroundOf('#nonced-probe'),
  };
}

/**
 * The DOM-injection and policy probes of AC7, run in the booted app. Each `attempt` returns the
 * thrown error's name (or '' when nothing was thrown) because the assertions are about WHICH
 * control refused the call: a TypeError is Trusted Types, and anything else is a different
 * (broken) outcome.
 *
 * `eval`/`new Function` are deliberately NOT probed here: this function is delivered through
 * the automation protocol, which is exempt from the document's eval restriction, so a call from
 * it proves nothing. That proof needs a script the policy governs, and it lives in the
 * "refuses string evaluation" test, which loads one from the app origin.
 */
function runTrustedTypesProbes(): TrustedTypesProbes {
  const probe = window as unknown as ProbeWindow;
  const attempt = (action: () => void): string => {
    try {
      action();
      return '';
    } catch (error) {
      return error instanceof Error ? error.name : 'unknown';
    }
  };

  const innerHtml = attempt(() => {
    document.body.innerHTML = '<img src=x onerror="window.__pwned=1">';
  });

  const script = document.createElement('script');
  const scriptUrl = attempt(() => {
    script.src = 'https://evil.example/x.js';
  });
  const scriptText = attempt(() => {
    script.textContent = 'window.__pwned=2';
  });

  // The policy probes need the factory itself; without it the suite cannot claim anything
  // about Trusted Types, so the missing implementation is an error rather than a pass.
  const globals = window as unknown as TrustedTypesGlobal;
  const types = globals.trustedTypes;
  if (types === undefined) {
    throw new Error('the browser exposes no Trusted Types implementation');
  }
  const evilPolicy = attempt(() => {
    types.createPolicy('evil', { createHTML: (html: string) => html });
  });
  // The bypass name is what `bypassSecurityTrust*` relies on: `trusted-types angular` must
  // refuse it, so the ban in the app origin is enforced by the browser and not only by lint.
  const unsafeBypassPolicy = attempt(() => {
    types.createPolicy('angular#unsafe-bypass', { createHTML: (html: string) => html });
  });
  // The positive half of the same allow-list, and the reason it needs two attempts. Angular 22
  // creates its `angular` policy lazily (core `getPolicy()` runs on the first sanitizer or
  // bypass call), so at boot the name is usually still free. If the creation succeeds, an
  // identical second one MUST be refused as a duplicate — that is the proof the name is
  // allow-listed rather than ignored. If it fails, the app already owns the name, and the
  // message must say so instead of "not allowed", which is what a blocked name says.
  const createAngularPolicy = (): PolicyAttempt => {
    try {
      types.createPolicy('angular', { createHTML: (html: string) => html });
      return { error: '', message: '' };
    } catch (error) {
      return {
        error: error instanceof Error ? error.name : 'unknown',
        message: error instanceof Error ? error.message : '',
      };
    }
  };
  const angularPolicy = createAngularPolicy();
  const angularPolicyDuplicate = createAngularPolicy().error;

  return {
    innerHtml,
    scriptUrl,
    scriptText,
    evilPolicy,
    unsafeBypassPolicy,
    angularPolicy,
    angularPolicyDuplicate,
    pwned: probe.__pwned ?? null,
    injectedImage: document.querySelector('img[src="x"]') !== null,
    policyNames: probe.__policyNames ?? [],
  };
}

/** Case-insensitive header lookup: neither `Response` nor `APIResponse` guarantees a case. */
function headerValueOrUndefined(source: HeaderSource, name: string): string | undefined {
  const wanted = name.toLowerCase();
  for (const [key, value] of Object.entries(source.headers())) {
    if (key.toLowerCase() === wanted) {
      return value;
    }
  }
  return undefined;
}

function headerValue(source: HeaderSource, name: string): string {
  const value = headerValueOrUndefined(source, name);
  if (value === undefined) {
    throw new Error(`the response carries no ${name} header`);
  }
  return value;
}

/** `a; b c; d` -> a directive name to its source tokens, in the order the server sent them. */
function parseDirectives(header: string): Map<string, string[]> {
  const directives = new Map<string, string[]>();
  for (const part of header.split(';')) {
    const tokens = part
      .trim()
      .split(/\s+/)
      .filter((token) => token !== '');
    const name = tokens[0];
    if (name === undefined) {
      continue;
    }
    directives.set(name.toLowerCase(), tokens.slice(1));
  }
  return directives;
}

function directive(directives: Map<string, string[]>, name: string): string[] {
  const values = directives.get(name);
  if (values === undefined) {
    throw new Error(`the policy carries no ${name} directive`);
  }
  return values;
}

/** The single nonce of a response, taken from the `style-src` nonce source of its own header. */
function nonceOf(source: HeaderSource): string {
  const directives = parseDirectives(headerValue(source, 'content-security-policy'));
  const nonceSources = directive(directives, 'style-src').filter((token) =>
    NONCE_SOURCE_PATTERN.test(token),
  );
  expect(nonceSources, 'exactly one nonce source in style-src').toHaveLength(1);
  const token = nonceSources[0] ?? '';
  return token.slice("'nonce-".length, -1);
}

function requiredResponse(response: PageResponse | null, label: string): PageResponse {
  if (response === null) {
    throw new Error(`no navigation response for ${label}`);
  }
  return response;
}

/** The value of `name` on a tag, quoted with either `"` or `'` (same parse as the budget gate). */
function attributeValue(tag: string, name: string): string | undefined {
  const match = new RegExp(`\\b${name}\\s*=\\s*(?:"([^"]*)"|'([^']*)')`, 'i').exec(tag);
  return match?.[1] ?? match?.[2];
}

/** The `.js` scripts and `.css` stylesheets the built index.html loads from its own locale. */
function referencedAssets(html: string): ReferencedAssets {
  const scripts: string[] = [];
  const styles: string[] = [];
  for (const tag of html.match(/<(?:script|link)\b[^>]*>/gi) ?? []) {
    const src = attributeValue(tag, 'src');
    if (src !== undefined) {
      if (src.endsWith('.js')) {
        scripts.push(src);
      }
      continue;
    }
    const href = attributeValue(tag, 'href');
    if (href !== undefined && href.endsWith('.css') && /rel\s*=\s*["']?stylesheet/i.test(tag)) {
      styles.push(href);
    }
  }
  return { scripts, styles };
}

/**
 * The document the inline-injection test navigates to. `script-src 'self'` has no nonce source,
 * so BOTH inline scripts must stay blocked; the nonced `<style>` proves the nonce path itself
 * works, which keeps the blocked-style assertions from passing vacuously.
 */
function probeDocument(nonce: string): string {
  return [
    '<!doctype html>',
    '<html lang="en">',
    '  <head><title>csp probe</title></head>',
    '  <body>',
    '    <div id="nonced-probe">nonced</div>',
    '    <p id="attr-probe" style="color: rgb(1, 2, 3)">attribute</p>',
    '    <script>window.__inlineScriptRan = true;</script>',
    `    <script nonce="${nonce}">window.__inlineScriptRan = 'nonced';</script>`,
    '    <style>body { background-color: rgb(4, 5, 6) }</style>',
    `    <style nonce="${nonce}">#nonced-probe { background-color: rgb(7, 8, 9) }</style>`,
    '  </body>',
    '</html>',
  ].join('\n');
}

/**
 * The document and its script for the no-`unsafe-eval` proof. The script is fetched from the
 * app origin, so `script-src 'self'` allows this policy-bound script and every `eval` it runs is
 * refused; the same calls made from `page.evaluate` are exempt from the policy (the automation
 * protocol is), so they cannot carry this assertion.
 */
function evalProbeDocument(): string {
  return [
    '<!doctype html>',
    '<html lang="en">',
    '  <head><title>eval probe</title></head>',
    '  <body>',
    '    <script src="csp-eval-probe.js"></script>',
    '  </body>',
    '</html>',
  ].join('\n');
}

const EVAL_PROBE_SCRIPT = [
  '(() => {',
  '  const attempt = (action) => {',
  "    try { action(); return ''; } catch (error) { return error.name; }",
  '  };',
  '  window.__evalProbe = {',
  "    evalCall: attempt(() => { if (eval('1') === 1) { return; } throw new Error('unexpected'); }),",
  '    functionConstructor: attempt(() => {',
  "      if (typeof new Function('return 1') === 'function') { return; }",
  "      throw new Error('unexpected');",
  '    }),',
  '  };',
  '})();',
].join('\n');

function readEvalProbe(): EvalProbeResult | null {
  const probe = window as unknown as EvalProbeWindow;
  return probe.__evalProbe ?? null;
}

for (const target of LOCALE_TARGETS) {
  test(`serves the app-origin CSP header on the document (${target.label})`, async ({ page }) => {
    const response = requiredResponse(await page.goto(target.path), target.path);
    expect(response.status()).toBe(200);

    const directives = parseDirectives(headerValue(response, 'content-security-policy'));

    // script-src is exactly `'self'`: no nonce (so no inline script can run), no unsafe-*, no
    // host source, nothing that would let a third-party script bundle itself in.
    expect(directive(directives, 'script-src')).toEqual(["'self'"]);
    expect(directive(directives, 'default-src')).toEqual(["'none'"]);
    expect(directive(directives, 'object-src')).toEqual(["'none'"]);
    expect(directive(directives, 'frame-ancestors')).toEqual(["'none'"]);
    expect(directive(directives, 'require-trusted-types-for')).toEqual(["'script'"]);
    // D-S66-Q1: the bypass policy name is deliberately absent, so `bypassSecurityTrust*` cannot
    // create the policy it needs in the app origin.
    expect(directive(directives, 'trusted-types')).toEqual(['angular']);

    const styleSrc = directive(directives, 'style-src');
    expect(styleSrc, "style-src keeps 'self'").toContain("'self'");
    const styleNonces = styleSrc.filter((token) => NONCE_SOURCE_PATTERN.test(token));
    expect(styleNonces, 'exactly one nonce source in style-src').toHaveLength(1);

    const connectSrc = directive(directives, 'connect-src');
    expect(connectSrc[0], "connect-src starts with 'self'").toBe("'self'");

    // Whole-policy sweep: no directive anywhere may carry a source that lets an inline script
    // or a string-evaluated script through, whatever directive the header grows later.
    for (const [name, values] of directives) {
      expect(values.join(' '), `${name} must not weaken the policy`).not.toMatch(
        /unsafe-inline|unsafe-eval/,
      );
      expect(values, `${name} must not carry a wildcard`).not.toContain('*');
    }

    // An HTML answer that carries a nonce must never be stored: a cached page would be served
    // with a nonce from another response.
    expect(headerValue(response, 'cache-control')).toContain('no-store');
  });

  test(`boots with no CSP violation, page error or console error (${target.label})`, async ({
    page,
  }) => {
    const consoleErrors: string[] = [];
    const pageErrors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') {
        consoleErrors.push(message.text());
      }
    });
    page.on('pageerror', (error) => {
      pageErrors.push(error.message);
    });
    // Before any page script: the listener must be in place from the first inline style Angular
    // inserts, otherwise a violation reported during bootstrap would be missed.
    await page.addInitScript(installProbe);

    await page.goto(target.path);
    await expect(page.locator('app-root h1')).toBeVisible();

    const state = await page.evaluate(readProbeState);
    expect(state.violations, 'CSP violations during bootstrap').toEqual([]);
    // `trusted-types angular` is an allow-list, so no name but `angular` can exist: a second
    // name here means the bundle created a bypass policy or another library smuggled one in.
    // Angular 22 creates `angular` lazily — core `getPolicy()` runs on the first sanitizer or
    // bypass call — so a boot that sanitizes nothing legitimately holds no policy at all; the
    // file's Trusted Types test proves the allow-listed name and refuses the other two.
    const otherPolicies = state.policyNames.filter((name) => name !== 'angular');
    expect(
      otherPolicies,
      `Trusted Types policies created by the page: ${state.policyNames.join(', ') || 'none'}`,
    ).toEqual([]);
    expect(pageErrors, 'page errors during bootstrap').toEqual([]);
    expect(consoleErrors, 'console errors during bootstrap').toEqual([]);
  });

  test(`serves the document with its own nonce applied (${target.label})`, async ({
    page,
    request,
  }) => {
    const response = requiredResponse(await page.goto(target.path), target.path);
    const nonce = nonceOf(response);
    expect(nonce).toMatch(NONCE_PATTERN);

    await expect(page.locator('app-root h1')).toBeVisible();
    const state = await page.evaluate(readPageProbeState);
    // `element.nonce`, never `getAttribute('nonce')`: the attribute is hidden from `getAttribute`
    // by design, so only the IDL property shows the nonce the browser will honour.
    expect(state.styleNonces, 'a <style> element carries the response nonce').toContain(nonce);
    expect(state.rootNonce, 'ngCspNonce on the app root').toBe(nonce);

    // The served source, not the DOM: a placeholder left in the document would mean the
    // injection ran on some pages only.
    const served = await request.get(target.path);
    const servedNonce = nonceOf(served);
    expect(servedNonce).toMatch(NONCE_PATTERN);
    const html = await served.text();
    expect(html, 'the placeholder is replaced in the served document').not.toContain(
      NONCE_PLACEHOLDER,
    );
    expect(html, 'the served document carries its own response nonce').toContain(servedNonce);
  });
}

test('gives every response its own nonce and keeps it out of the bundle assets', async ({
  request,
}) => {
  const localePath = '/en/';
  const first = await request.get(localePath);
  const second = await request.get(localePath);
  const firstNonce = nonceOf(first);
  const secondNonce = nonceOf(second);
  expect(firstNonce).toMatch(NONCE_PATTERN);
  expect(secondNonce).toMatch(NONCE_PATTERN);
  expect(firstNonce, 'two responses must not share a nonce').not.toBe(secondNonce);

  const firstBody = await first.text();
  const secondBody = await second.text();
  // Each body carries its own nonce and only its own: a nonce reused from another request is
  // exactly what a cached or shared header would produce.
  expect(firstBody).toContain(firstNonce);
  expect(firstBody).not.toContain(secondNonce);
  expect(secondBody).toContain(secondNonce);
  expect(secondBody).not.toContain(firstNonce);
  expect(firstBody).not.toContain(NONCE_PLACEHOLDER);
  expect(secondBody).not.toContain(NONCE_PLACEHOLDER);

  const assets = referencedAssets(firstBody);
  expect(assets.scripts.length, 'initial <script src> in the built index.html').toBeGreaterThan(0);
  expect(assets.styles.length, '<link rel="stylesheet"> in the built index.html').toBeGreaterThan(
    0,
  );

  const documentUrl = new URL(first.url());
  for (const reference of [...assets.scripts, ...assets.styles]) {
    const assetUrl = new URL(reference, documentUrl).toString();
    const assetResponse = await request.get(assetUrl);
    expect(assetResponse.status(), assetUrl).toBe(200);

    const assetNonce = nonceOf(assetResponse);
    expect(assetNonce, `${assetUrl} carries its own nonce`).toMatch(NONCE_PATTERN);
    expect(assetNonce, `${assetUrl} must not reuse a document nonce`).not.toBe(firstNonce);
    expect(assetNonce, `${assetUrl} must not reuse a document nonce`).not.toBe(secondNonce);

    // A nonce written into an asset would be a nonce the page never generated.
    const assetBody = await assetResponse.text();
    expect(assetBody, `${assetUrl} must not carry a nonce`).not.toContain(firstNonce);
    expect(assetBody, `${assetUrl} must not carry a nonce`).not.toContain(secondNonce);
    expect(assetBody, `${assetUrl} must not carry the placeholder`).not.toContain(
      NONCE_PLACEHOLDER,
    );
  }
});

test('blocks inline script and style that carry no allowed nonce', async ({ page, request }) => {
  const documentResponse = await request.get('/en/');
  const policy = headerValue(documentResponse, 'content-security-policy');
  const nonce = nonceOf(documentResponse);

  await page.addInitScript(installProbe);
  // Served from the app origin, so the browser enforces this policy on this document exactly as
  // it would on the app's own pages; the header is the one a real navigation received.
  await page.route('**/csp-probe.html', (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: 'text/html',
      headers: { 'content-security-policy': policy, 'cache-control': 'no-store' },
      body: probeDocument(nonce),
    }),
  );
  await page.goto('/csp-probe.html');

  // Two blocked scripts, a blocked <style> and a blocked style attribute: four violations, but a
  // count is the wrong thing to wait on — it says nothing about WHICH event arrived last, so the
  // assertions below could read a list that already has four entries of the wrong kinds. Wait for
  // exactly the directives they inspect (accepting either spelling of the element directives)
  // and for the document to finish loading, instead of a fixed sleep.
  await page.waitForFunction(() => {
    const probe = window as unknown as ProbeWindow;
    if (document.readyState !== 'complete') return false;
    const blocked = new Set((probe.__violations ?? []).map((violation) => violation.directive));
    const scriptBlocked = blocked.has('script-src-elem') || blocked.has('script-src');
    const styleBlocked = blocked.has('style-src-elem') || blocked.has('style-src');
    return scriptBlocked && styleBlocked && blocked.has('style-src-attr');
  });

  const state = await page.evaluate(readProbeState);
  expect(state.inlineScriptRan, 'neither inline script may run').toBeNull();

  const blockedDirectives = new Set(state.violations.map((violation) => violation.directive));
  const blocked = [...blockedDirectives];
  expect(
    blocked.some((name) => name === 'script-src-elem' || name === 'script-src'),
    `an inline script was refused (saw ${blocked.join(', ')})`,
  ).toBe(true);
  expect(
    blocked.some((name) => name === 'style-src-elem' || name === 'style-src'),
    `an unnonced <style> was refused (saw ${blocked.join(', ')})`,
  ).toBe(true);
  expect(blocked, `a style attribute was refused (saw ${blocked.join(', ')})`).toContain(
    'style-src-attr',
  );

  const styles = await page.evaluate(readProbeStyles);
  expect(styles.bodyBackground, 'an unnonced <style> does not apply').not.toBe(
    UNNONCED_STYLE_BACKGROUND,
  );
  expect(styles.attributeColor, 'a style attribute does not apply').not.toBe(STYLE_ATTRIBUTE_COLOR);
  // The nonced <style> DOES apply: the policy allows exactly one nonce source, so this proves
  // the blocked declarations above were refused for the missing nonce and not because styles
  // were off altogether.
  expect(styles.noncedBackground, 'a nonced <style> does apply').toBe(NONCED_STYLE_BACKGROUND);
});

test('enforces Trusted Types and blocks DOM injection in the booted app', async ({ page }) => {
  await page.addInitScript(installProbe);
  await page.goto('/en/');
  await expect(page.locator('app-root h1')).toBeVisible();

  const probes = await page.evaluate(runTrustedTypesProbes);

  // Trusted Types: every string assignment to a script/HTML sink is refused, which is what
  // `require-trusted-types-for 'script'` guarantees and what an Angular template binding never
  // needs, because the framework goes through the `angular` policy.
  expect(probes.innerHtml, 'document.body.innerHTML assignment').toBe('TypeError');
  expect(probes.scriptUrl, 'script.src assignment').toBe('TypeError');
  expect(probes.scriptText, 'script.textContent assignment').toBe('TypeError');
  expect(probes.pwned, 'no injected handler may run').toBeNull();
  expect(probes.injectedImage, 'the injected <img> must not be in the DOM').toBe(false);

  // String evaluation is refused too, but it cannot be proven from this context (the automation
  // protocol is exempt from the policy); the "refuses string evaluation" test below runs the
  // same two calls from a script the policy governs.
  //
  // `trusted-types angular`: no other policy name can be created, including the bypass name
  // `bypassSecurityTrust*` requires (D-S66-Q1). The allow-listed name itself is creatable, so
  // the two refusals are an allow-list and not "Trusted Types is off".
  expect(probes.evilPolicy, "createPolicy('evil')").toBe('TypeError');
  expect(probes.unsafeBypassPolicy, "createPolicy('angular#unsafe-bypass')").toBe('TypeError');
  if (probes.angularPolicy.error === '') {
    // The name was still free — Angular creates its policy lazily — so the page now owns it,
    // and the identical second attempt must be refused as a duplicate.
    expect(probes.angularPolicyDuplicate, 'a duplicate angular policy is refused').toBe(
      'TypeError',
    );
  } else {
    // The app already owns the name; the refusal must say so, not "not allowed", which is what
    // a name outside the `trusted-types` allow-list answers.
    expect(probes.angularPolicy.error, "createPolicy('angular')").toBe('TypeError');
    expect(probes.angularPolicy.message, 'the angular policy already exists').toMatch(
      /already exists/i,
    );
  }
  // Exactly one `angular` policy exists — the app's, or the one this probe just created when
  // the lazily created app policy had not been needed yet — and nothing else was accepted.
  expect(probes.policyNames, 'the angular policy is the only policy name in this document').toEqual(
    ['angular'],
  );

  // The probes must not have damaged the booted app.
  await expect(page.locator('app-root h1')).toBeVisible();
});

test('refuses string evaluation under the app policy (no unsafe-eval)', async ({
  page,
  request,
}) => {
  // The policy is the one a real document received, and the script is fetched from the app
  // origin, so `script-src 'self'` lets it run as a POLICY-BOUND script: the eval calls below
  // are then refused exactly as they would be inside the bundle.
  const documentResponse = await request.get('/en/');
  const policy = headerValue(documentResponse, 'content-security-policy');

  await page.route('**/csp-eval-probe.js', (route: Route) =>
    route.fulfill({ status: 200, contentType: 'text/javascript', body: EVAL_PROBE_SCRIPT }),
  );
  await page.route('**/csp-eval-probe.html', (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: 'text/html',
      headers: { 'content-security-policy': policy, 'cache-control': 'no-store' },
      body: evalProbeDocument(),
    }),
  );

  await page.goto('/csp-eval-probe.html');
  await page.waitForFunction(() => {
    const probe = window as unknown as EvalProbeWindow;
    return probe.__evalProbe !== undefined;
  });

  const probe = await page.evaluate(readEvalProbe);
  expect(probe, 'the policy-bound probe script ran').not.toBeNull();
  // EvalError is Chromium's refusal for both calls; TypeError is accepted because a
  // Trusted-Types refusal of the same call raises that instead.
  expect(probe?.evalCall, 'eval() in a policy-bound script').toMatch(/^(EvalError|TypeError)$/);
  expect(probe?.functionConstructor, 'new Function() in a policy-bound script').toMatch(
    /^(EvalError|TypeError)$/,
  );
});
