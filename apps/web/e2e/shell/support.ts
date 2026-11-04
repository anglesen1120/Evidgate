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

/**
 * `withVariant` over several queries, applied left to right: a case that needs two overrides at
 * once (`listPermissions` for the grants, `removeMember` for the answer) passes both here instead of
 * re-spelling the `?fixture.variant.` prefix.
 */
export function withVariants(path: string, variantQueries: readonly string[]): string {
  return variantQueries.reduce(withVariant, path);
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

/**
 * The Helm dialog fades and zooms in on open (`styles/helm/dialog.css`). axe measures computed
 * colours, so a run started mid-entrance reads the blended opacity and reports contrast the settled
 * dialog does not have. Waits for the dialog's finite animations instead; an infinite one (a
 * progress spinner) never settles and is skipped rather than awaited forever.
 */
export async function waitForDialogAnimations(page: Page, dialog: string): Promise<void> {
  await page.locator(dialog).first().waitFor({ state: 'attached' });
  await page.evaluate(async (selector) => {
    const element = document.querySelector(selector);
    if (element === null) return;
    // The backdrop animates in the overlay container around the dialog, not inside it.
    const root = element.closest('.cdk-overlay-container') ?? element;
    const animations = root.getAnimations({ subtree: true }).filter((animation) => {
      const timing = animation.effect?.getComputedTiming();
      return timing !== undefined && Number.isFinite(timing.iterations);
    });
    await Promise.all(animations.map((animation) => animation.finished.catch(() => undefined)));
  }, dialog);
}

// ---------------------------------------------------------------------------------------------
// Settings shell (09.7.1-T1). The tab strip's DOM contract is fixed by the plan (09.7.1 §10 T1.1):
// `app-settings-shell` hosts the strip, each visible tab is a link carrying
// `data-settings-tab="<tab.id>"`, the active one carries `aria-current="page"`, a tab whose story
// has not shipped a body yet answers with `app-settings-tab-placeholder[data-settings-tab-body=
// "<tab.id>"]` (09.7.1-T2 replaced the members body with `app-members-page`) and a session with no
// tab at all gets `app-settings-no-tabs`. The grant sets below are corpus variants
// (`fixtures/web-api/permissions-list-settings-*`), never a role name: the client derives the
// grants from the `tenant_assignable` catalogue rows alone.
// ---------------------------------------------------------------------------------------------

/** `?fixture.variant.listPermissions=settings-admin` — every tab code of the strip is granted. */
export const SETTINGS_ADMIN_GRANTS = '?fixture.variant.listPermissions=settings-admin';

/** `?fixture.variant.listPermissions=settings-audit-only` — `organization:read` + `audit:read`. */
export const SETTINGS_AUDIT_ONLY_GRANTS = '?fixture.variant.listPermissions=settings-audit-only';

/** `?fixture.variant.listPermissions=settings-no-tab` — `organization:read` alone, so no tab. */
export const SETTINGS_NO_TAB_GRANTS = '?fixture.variant.listPermissions=settings-no-tab';

/** `?fixture.variant.listPermissions=usage-read` — `organization:read` and `usage:read`, the Usage page's one grant. */
export const USAGE_READ_GRANTS = '?fixture.variant.listPermissions=usage-read';

/**
 * `?fixture.variant.listPermissions=settings-members-readonly` — `membership:read`,
 * `organization:read` and `role:read`, so the Members tab opens while `membership:write` is absent.
 */
export const MEMBERS_READONLY_GRANTS = '?fixture.variant.listPermissions=settings-members-readonly';

// ---------------------------------------------------------------------------------------------
// Members and roles tab (09.7.1-T2). Its DOM contract is the plan's §10 T2 hooks:
// `app-members-page[data-members-phase]`, rows `[data-members-row][data-membership-id]`, the
// matrix `[data-members-matrix]`, `[data-members-invite-open]` / `[data-members-remove]`, native
// dialogs `[data-members-dialog="invite|remove"]`, `[data-members-submit]` and the mutation error
// region `[data-members-error]` + `[data-members-error-code]`. Every variant below is a corpus
// directory (`fixtures/web-api/membership*`), selected per operation.
// ---------------------------------------------------------------------------------------------

/** `?fixture.variant.listMemberships=empty` — the members page with no membership at all. */
export const MEMBERS_EMPTY_LIST = '?fixture.variant.listMemberships=empty';

/** `?fixture.variant.createMembership=invite-exists` — the 409 `invite_exists` of an invite. */
export const MEMBERS_INVITE_EXISTS = '?fixture.variant.createMembership=invite-exists';

/** `?fixture.variant.removeMember=last-admin` — the 409 `last_admin` of AC2. */
export const MEMBERS_REMOVE_LAST_ADMIN = '?fixture.variant.removeMember=last-admin';

/** `?fixture.variant.removeMember=invalid-transition` — the 409 `invalid_transition` of a pending. */
export const MEMBERS_REMOVE_INVALID_TRANSITION = '?fixture.variant.removeMember=invalid-transition';

// ---------------------------------------------------------------------------------------------
// Members and roles tab: the role-edit dialog (09.7.1-F6). Its DOM contract is the plan's §10
// F6 hooks: one row button `[data-members-edit-open]`, the native dialog
// `[data-members-dialog="edit-roles"]` with a checkbox group `[data-members-role-option]
// [data-role-id]`, `[data-members-submit]`, the verbatim error region `[data-members-error]` +
// `[data-members-error-code]`, the static last-admin hint `[data-members-last-admin-hint]` and the
// partial-apply summary `[data-members-applied-summary]` with one
// `[data-members-applied-item]` / `[data-members-not-applied-item]` per role change. The three
// variants below are the corpus directories `fixtures/web-api/membership-role-*`, selected per
// operation: the role edit is four calls (`getMembership`, grant, revoke, and the reads the tab
// already makes), so a case that needs two answers at once (`grantMembershipRole` for the change,
// `revokeMembershipRole` for its refusal) passes both to `withVariants`.
// ---------------------------------------------------------------------------------------------

/**
 * `?fixture.variant.grantMembershipRole=forbidden` — the 403 `forbidden` of the self-escalation
 * rule (00 §2: a role never widens the caller's own access), with the server's own message.
 */
export const MEMBERS_GRANT_FORBIDDEN = '?fixture.variant.grantMembershipRole=forbidden';

/** `?fixture.variant.grantMembershipRole=unknown-role` — that operation's 409 `unknown_role`. */
export const MEMBERS_GRANT_UNKNOWN_ROLE = '?fixture.variant.grantMembershipRole=unknown-role';

/**
 * `?fixture.variant.revokeMembershipRole=last-admin` — the 409 `last_admin` of demoting the org's
 * last active admin, the role-edit counterpart of `MEMBERS_REMOVE_LAST_ADMIN`.
 */
export const MEMBERS_REVOKE_LAST_ADMIN = '?fixture.variant.revokeMembershipRole=last-admin';

/** `/orgs/:orgId/settings` — the shell's index address, which redirects to the first open tab. */
export function settingsPath(locale: ShellLocale, orgId: string = ORG_ACME): string {
  return localePath(locale, `/orgs/${orgId}/settings`);
}

/** `/orgs/:orgId/settings/:tab` — one tab of the shell; `tab` is a `SettingsTabId` segment. */
export function settingsTabPath(
  locale: ShellLocale,
  tab: string,
  orgId: string = ORG_ACME,
): string {
  return localePath(locale, `/orgs/${orgId}/settings/${tab}`);
}

/**
 * `/orgs/:orgId/settings/members` — the Members and roles tab (09.7.1-T2). Spelled once so the
 * members suite never repeats the segment or the shell's path shape.
 */
export function membersPath(locale: ShellLocale, orgId: string = ORG_ACME): string {
  return settingsTabPath(locale, 'members', orgId);
}

/**
 * The `data-settings-tab` values of the strip, in DOM order — the exact set and order the session
 * may see. Read from the attribute, never from a label, so both locale bundles answer the same.
 */
export async function settingsTabIds(page: Page): Promise<string[]> {
  return page
    .locator('app-settings-shell [data-settings-tab]')
    .evaluateAll((tabs) => tabs.map((tab) => tab.getAttribute('data-settings-tab') ?? ''));
}

/** The `data-settings-tab` of the active tab (`aria-current="page"`), or `null` when none is. */
export async function activeSettingsTabId(page: Page): Promise<string | null> {
  const active = await page
    .locator('app-settings-shell [data-settings-tab][aria-current="page"]')
    .evaluateAll((tabs) => tabs.map((tab) => tab.getAttribute('data-settings-tab') ?? ''));
  return active[0] ?? null;
}

/**
 * The `data-settings-tab-body` of the rendered tab body, or `null` when no body is rendered.
 *
 * This answers the PLACEHOLDER bodies only: a tab whose story has shipped renders its own host
 * (`app-members-page` for `members`, 09.7.1-T2), which deliberately carries no
 * `data-settings-tab-body` — its own hooks (`data-members-phase`) say which state it is in. A case
 * that visits a real tab body asserts that host instead.
 */
export async function settingsTabBodyId(page: Page): Promise<string | null> {
  const bodies = await page
    .locator('app-settings-tab-placeholder[data-settings-tab-body]')
    .evaluateAll((list) => list.map((body) => body.getAttribute('data-settings-tab-body') ?? ''));
  return bodies[0] ?? null;
}

/** What an address rendered, for the identity comparison a 404 claim needs. */
export interface RenderedAddress {
  readonly pathname: string;
  readonly url: string;
  readonly heading: string;
  readonly text: string;
}

/**
 * Opens each address, records what the page says, and asserts each one rendered the not-found
 * pattern IN PLACE: same address still in the bar, one heading and one body text for all of them,
 * and never the forbidden page (a refusal leaking into the 404 copy would confirm the tab exists).
 *
 * Shared by the shell's tab-strip suite (09.7.1-T1) and the Members tab's (09.7.1-T2), because
 * both make the same claim: a destination the caller may not open is indistinguishable from one
 * that never existed.
 */
export async function expectIndistinguishableNotFound(
  page: Page,
  attempts: readonly string[],
): Promise<readonly RenderedAddress[]> {
  const rendered: RenderedAddress[] = [];
  for (const attempted of attempts) {
    const address = new URL(attempted, 'http://127.0.0.1');
    await page.goto(attempted);
    await expect(page.locator('#main-content h1')).toHaveCount(1);
    await expect(page.locator('app-not-found-pattern')).toHaveCount(1);
    await expect(page.locator('app-forbidden-pattern')).toHaveCount(0);
    rendered.push({
      pathname: address.pathname,
      url: page.url(),
      heading: (await page.locator('#main-content h1').innerText()).trim(),
      text: await mainText(page),
    });
  }

  for (const [index, entry] of rendered.entries()) {
    const landed = new URL(entry.url).pathname;
    expect(landed, `attempt ${index} kept the address it was given`).toBe(entry.pathname);
    expect(landed.endsWith('/forbidden'), `attempt ${index} is not the refusal page`).toBe(false);
  }

  expect(new Set(rendered.map((entry) => entry.heading)).size).toBe(1);
  expect(new Set(rendered.map((entry) => entry.text)).size).toBe(1);
  return rendered;
}

// 09.7.1-F12 invite
/** A stored create answer carries no one-time invite token. */
export const MEMBERS_CREATE_REPLAY = '?fixture.variant.createMembership=replay';
/** The pending invitation's fresh token, answered by the resend corpus document. */
export const MEMBERS_RESEND_OK = '?fixture.variant.resendMembershipInvite=ok';
/** A stored resend answer carries no one-time invite token. */
export const MEMBERS_RESEND_REPLAY = '?fixture.variant.resendMembershipInvite=replay';
/** The server's 409 when the invitation is no longer pending. */
export const MEMBERS_RESEND_NOT_PENDING = '?fixture.variant.resendMembershipInvite=not-pending';
/** The bodyless 204 that confirms the pending invitation was cancelled. */
export const MEMBERS_CANCEL_OK = '?fixture.variant.cancelMembershipInvite=ok';
/** The cancel operation's own 409 when the invitation is no longer pending. */
export const MEMBERS_CANCEL_NOT_PENDING = '?fixture.variant.cancelMembershipInvite=not-pending';

// 09.7.1-T3 secrets
/** The full read/write grant set; settings-admin intentionally grants metadata read only. */
export const SECRETS_WRITE_GRANTS = '?fixture.variant.listPermissions=settings-secrets-write';
/** Six metadata-only references covering active, rotated, revoked and reminder states. */
export const SECRETS_LIST = '?fixture.variant.listIngestTokens=settings';
/** The rotation operation's server refusal, not a list-read failure. */
export const SECRETS_ROTATE_CONFLICT = '?fixture.variant.rotateIngestToken=conflict';
/** A retryable burst limit with a 30-second Retry-After, without minting a credential. */
export const SECRETS_ROTATE_RATE_LIMITED = '?fixture.variant.rotateIngestToken=rate-limited';
/** Review-only address; the real G2 product route remains absent in this G1 build. */
export function secretsReviewPath(locale: ShellLocale, orgId: string = ORG_ACME): string {
  return localePath(locale, `/dev/settings-secrets/orgs/${orgId}`);
}
