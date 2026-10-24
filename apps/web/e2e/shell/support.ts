// 09.1.1-T4.4 (plan 09.1.1 §10 T4, §2 AC4/AC5/AC6; 00 §5.6 W9): the shared pieces of the shell's
// real-browser suite, which runs against the FIXTURES build (`playwright.fixtures.config.ts`).
//
// Three rules keep these specs meaningful:
//
// 1. **No localized string identifies an element.** Every assertion keys on the stable thing—a
//    route segment, a role, an id, a `data-*` hook, the `<html lang>` — so the same spec passes on
//    the `vi` bundle and on the `en` bundle. A label is only ever asserted where the test is about
//    that label.
// 2. **The URL under test is the app's real one**: each locale bundle is served from its own
//    `<base href="/<locale>/">`, so every path here starts with `/en` or `/vi`. `localePath`
//    builds it in one place.
// 3. **Fixtures pick the session state, never the app.** `?fixture.variant.<operationId>=<variant>`
//    is read by the dev fixture backend (`core/dev-fixtures/fixture-toggles.ts`); the app's own
//    query handling never sees a session fact. The variants below name corpus directories that
//    exist in `fixtures/web-api/`.
import { expect, type Page } from '@playwright/test';
import axe from 'axe-core';
import type { AxeResults, RunOptions } from 'axe-core';

/** The org and project ids of `fixtures/web-api/orgs-list` + `projects-list` (org 1). */
export const ORG_ACME = '018f0000-0000-7000-8000-000000000001';
/** The second organization of `orgs-list`, used by the scope-switcher cases. */
export const ORG_LUMEN = '018f0000-0000-7000-8000-000000000002';
/** The first project of org 1 (`Storefront web`). */
export const PROJECT_STOREFRONT = '018f0000-0000-7000-8000-000000000011';
/** The second project of org 1 (`Checkout API`). */
export const PROJECT_CHECKOUT = '018f0000-0000-7000-8000-000000000012';
/** An id no list in the corpus holds: a foreign organization. */
export const UNKNOWN_ORG = '018f0000-0000-7000-8000-000000000099';
/** The same for a project: well-formed, never listed under any organization. */
export const UNKNOWN_PROJECT = '018f0000-0000-7000-8000-000000000098';

/** The two bundles the fixtures build emits (`en` is the source locale, `vi` the product default). */
export const LOCALES = ['en', 'vi'] as const;
export type ShellLocale = (typeof LOCALES)[number];

/** The themes the contrast matrix covers: `light` is the token set with no `data-theme`. */
export const THEMES = ['light', 'dark'] as const;
export type ShellTheme = (typeof THEMES)[number];

/**
 * The fixture variant that trims the grant set (`permissions-list-limited`): the shell then holds
 * `audit:read`, `authoring:read`, `membership:read`, `project:read` and `role:read` — and nothing
 * else, so every other G1 entry is hidden and its route refuses.
 */
export const LIMITED_GRANTS = '?fixture.variant.listPermissions=limited';

/** The corpus's 401 for the session read (`orgs-list-unauthenticated`). */
export const ANONYMOUS_SESSION = '?fixture.variant.listMyOrganizations=unauthenticated';

/** The corpus's 503 for the session read (`orgs-list-unavailable`). */
export const UNREADABLE_SESSION = '?fixture.variant.listMyOrganizations=unavailable';

/**
 * An absolute path inside one locale bundle. The build writes one directory per locale with its
 * own `<base href>`, so the prefix is not optional: `/orgs/…` belongs to no bundle.
 */
export function localePath(locale: ShellLocale, path: string): string {
  const suffix = path.startsWith('/') ? path : `/${path}`;
  return `/${locale}${suffix}`;
}

/** `/orgs/:orgId` — the org-scope landing address. */
export function orgPath(locale: ShellLocale, orgId: string = ORG_ACME): string {
  return localePath(locale, `/orgs/${orgId}`);
}

/** `/orgs/:orgId/projects/:projectId/overview` — the one project screen the shell ships. */
export function overviewPath(
  locale: ShellLocale,
  orgId: string = ORG_ACME,
  projectId: string = PROJECT_STOREFRONT,
): string {
  return localePath(locale, `/orgs/${orgId}/projects/${projectId}/overview`);
}

/**
 * Appends a fixture variant to a path, keeping any query the caller already put on it. The order
 * of the parameters is the caller's; the fixture backend reads them from `location.search`.
 */
export function withVariant(path: string, variantQuery: string): string {
  const [base, existing] = path.split('?', 2);
  const extra = variantQuery.startsWith('?') ? variantQuery.slice(1) : variantQuery;
  if (extra === '') {
    return path;
  }
  if (existing === undefined || existing === '') {
    return `${base}?${extra}`;
  }
  return `${base}?${existing}&${extra}`;
}

/** The base URL the worker's config resolved, for the cases that open their own context. */
export function fixturesBaseUrl(): string {
  const port = process.env['EVIDGATE_WEB_FIXTURES_E2E_PORT'];
  if (port === undefined || port === '') {
    throw new Error(
      'EVIDGATE_WEB_FIXTURES_E2E_PORT is unset: run this suite through playwright.fixtures.config.ts ' +
        '(npm run e2e:fixtures -w apps/web), which pins the port for its workers.',
    );
  }
  return `http://127.0.0.1:${port}`;
}

/** The segments of the sidebar's links, sorted: the shell's whole navigation, locale-free. */
export async function navSegments(page: Page): Promise<string[]> {
  const hrefs = await page
    .locator('app-shell-nav nav a')
    .evaluateAll((links) => links.map((link) => link.getAttribute('href') ?? ''));
  return hrefs
    .map(
      (href) =>
        href
          .split('/')
          .filter((part) => part !== '')
          .at(-1) ?? '',
    )
    .sort();
}

/** The visible text of the route's own region (`<main id="main-content">`), collapsed. */
export async function mainText(page: Page): Promise<string> {
  return (await page.locator('#main-content').innerText()).replace(/\s+/g, ' ').trim();
}

/** What currently holds focus, in the shape a failing assertion can print. */
export async function activeElement(page: Page): Promise<{
  tag: string;
  id: string;
  className: string;
  text: string;
}> {
  return page.evaluate(() => {
    const element = document.activeElement;
    return {
      tag: element?.tagName.toLowerCase() ?? '',
      id: element?.id ?? '',
      className: element?.className ?? '',
      text: (element?.textContent ?? '').trim().slice(0, 80),
    };
  });
}

/**
 * The explicit theme the app put on `<html>`, or `null` when the visitor never chose one. `null` is
 * the meaningful default: the tokens then follow `prefers-color-scheme` and the attribute must stay
 * absent, so a stylesheet can never tell "light by choice" from "light by system".
 */
export async function currentTheme(page: Page): Promise<string | null> {
  return page.evaluate(() => document.documentElement.getAttribute('data-theme'));
}

/**
 * Stores (or clears) the explicit theme choice before the app bootstraps, the way a returning
 * visitor's `localStorage` would already hold it. `ThemeService` reads the key once in its
 * constructor, so this has to run before the page's own scripts — which is what `addInitScript`
 * guarantees. Pass `null` for "no choice was ever made".
 */
export async function seedStoredTheme(page: Page, theme: ShellTheme | null): Promise<void> {
  await page.addInitScript((stored) => {
    if (stored === null) {
      localStorage.removeItem('evidgate-theme');
    } else {
      localStorage.setItem('evidgate-theme', stored);
    }
  }, theme);
}

/** `axe-core`'s WCAG 2.2 A/AA tag set: everything the product claims, contrast included. */
const AXE_OPTIONS: RunOptions = {
  runOnly: {
    type: 'tag',
    values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'],
  },
};

/** The subset of the injected `axe` global these specs use. */
interface AxeGlobal {
  run: (context: Document, options: RunOptions) => Promise<AxeResults>;
}

/**
 * Runs axe in the real page, in real Chromium, with `color-contrast` ENABLED — the difference to
 * the jsdom gallery spec, which has no layout and must disable that rule.
 *
 * axe is injected from the workspace's own `axe-core` devDependency (`axe.source`), so no package
 * is added and the rule set is the version the repo pins.
 */
export async function axeScan(page: Page, context?: string): Promise<AxeResults> {
  await page.addScriptTag({ content: axe.source });
  return page.evaluate(
    async ({ context, options }: { context: string | undefined; options: RunOptions }) => {
      // `window.axe` is the global the injected script just defined; axe-core ships no typing for
      // the browser copy it injects, so this is the boundary where the shape is known.
      const axeGlobal = window as typeof window & { axe: AxeGlobal };
      // `context` is a CSS selector string here; axe resolves it inside the page.
      return axeGlobal.axe.run((context ?? document) as Document, options);
    },
    { context, options: AXE_OPTIONS },
  );
}

/** One axe finding, trimmed to what a report can quote. */
export interface AxeFinding {
  readonly id: string;
  readonly impact: string | null;
  /** axe's selector list; a frame-crossing selector is itself an array, so the shape stays open. */
  readonly targets: readonly unknown[];
  readonly summaries: readonly string[];
}

/** The violations of a scan, in the same shape the gallery's jsdom spec reports them. */
export function axeViolations(results: AxeResults): AxeFinding[] {
  return results.violations.map((violation) => ({
    id: violation.id,
    impact: violation.impact ?? null,
    targets: violation.nodes.map((node) => node.target),
    summaries: violation.nodes.map((node) => node.failureSummary ?? ''),
  }));
}

/**
 * Asserts a page is WCAG 2.2 A/AA clean AND that the colour-contrast rule actually produced an
 * outcome (a pass or an "incomplete"): a silent rule would make "no violations" meaningless.
 */
export async function expectNoA11yViolations(page: Page, context: string): Promise<void> {
  const results = await axeScan(page);
  const contrast = [...results.passes, ...results.incomplete].filter(
    (rule) => rule.id === 'color-contrast',
  );
  expect(
    contrast.length,
    `axe evaluated color-contrast on ${context} (violations: ${results.violations.length})`,
  ).toBeGreaterThan(0);
  expect(axeViolations(results), `axe violations on ${context}`).toEqual([]);
}
