// Connection metadata and whole-page checks for the one-time reveal boundary.
import { expect, type Page } from '@playwright/test';

/** `fixtures/web-api/ingest-token-create/input.json` → `body.secret`. */
export const FIXTURE_TOKEN_SECRET = 'egt_FIXTURE_ONLY_not_a_real_token';

/** `fixtures/web-api/ingest-token-rotate/input.json` → `body.secret`. */
export const FIXTURE_ROTATED_TOKEN_SECRET = 'egt_FIXTURE_ONLY_not_a_real_rotated_token';

/** `fixtures/web-api/ci-binding-create/input.json` → `body.binding_challenge`. */
export const FIXTURE_BINDING_CHALLENGE = 'evg_fixture_only_binding_challenge_not_a_real_value';

// Connection health and CLI quickstart read the same two metadata
// rows the panel already renders, so the facts the health suite asserts are the corpus's own:
// `fixtures/web-api/ingest-tokens-list-onboarding-token-used/input.json` → `body.items[0]`.

/** That fixture's token name. The quickstart comment quotes it and the credential row titles it. */
export const TOKEN_USED_NAME = 'Release CI';
/** That fixture's prefix. The quickstart names it; it is also the rotate button's label text. */
export const TOKEN_USED_PREFIX = 'egt_fixture';
/** That fixture's `last_used_at`, rendered in the panel's `<time [attr.datetime]>`. */
export const TOKEN_USED_LAST_USED_AT = '2026-10-24T09:12:34Z';
/** The project slug of `project-detail-onboarding-project`: the quickstart's `EVIDGATE_PROJECT`. */
export const ONBOARDING_PROJECT_SLUG = 'storefront-web';

/** The step-2 page (`app-onboarding-connect-ci`), rendered for `viewStep === 2` with a project. */
export function connectCiPage(page: Page) {
  return page.locator('[data-testid="connect-ci-page"]');
}

/**
 * The one-time reveal wrapper — the single subtree allowed to hold a secret, and only while the
 * component-scoped `RevealSlot` holds one.
 */
export function revealSlot(page: Page) {
  return page.locator('[data-testid="connect-reveal"]');
}

/** The read-only control the reveal renders the one-time value in. */
export function revealValue(page: Page) {
  return page.locator('[data-testid="reveal-value"]');
}

/**
 * A one-time value lives in exactly one place — the `connect-reveal` subtree — and only for
 * as long as that subtree is mounted. This is the whole-page scan behind that claim, and it looks
 * in the three places a value can hide, because a text-only scan would pass vacuously:
 *
 *   - **every attribute** of every element (a title, an `aria-label`, a `data-*`, the `[value]`
 *     binding of any input);
 *   - **every form control's `value` property** — Angular's `[value]` sets the property, not the
 *     attribute, so an input that kept a copy is invisible to an attribute scan;
 *   - **every rendered text node**, skipping `<script>`/`<style>`, whose text is source, not render.
 *
 * Plus the four out-of-document places a secret should never reach: both storages, `document.cookie`,
 * the address bar and `history.state`.
 *
 * `holders` names every element that holds the value (should be exactly the reveal's control while
 * the slot is open, and nothing at all after it closes); `outside` names those of them — and those
 * out-of-document places — that are NOT inside `connect-reveal`, which must always be empty.
 */
export async function secretFootprint(
  page: Page,
  secret: string,
): Promise<{ holders: readonly string[]; outside: readonly string[] }> {
  return page.evaluate((value) => {
    const reveal = '[data-testid="connect-reveal"]';
    const holders = new Set<string>();
    const outside = new Set<string>();
    const name = (element: Element): string => {
      const testId = element.getAttribute('data-testid');
      if (testId !== null) return `${element.tagName.toLowerCase()}[${testId}]`;
      return element.id === ''
        ? element.tagName.toLowerCase()
        : `${element.tagName.toLowerCase()}#${element.id}`;
    };
    const record = (element: Element, where: string): void => {
      holders.add(`${name(element)} ${where}`);
      if (element.closest(reveal) === null) outside.add(`${name(element)} ${where}`);
    };
    for (const element of Array.from(document.querySelectorAll('*'))) {
      if (element.tagName === 'SCRIPT' || element.tagName === 'STYLE') continue;
      for (const attribute of Array.from(element.attributes)) {
        if (attribute.value.includes(value)) record(element, attribute.name);
      }
      if (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement) {
        if (element.value.includes(value)) record(element, 'value');
      }
      if (element instanceof HTMLSelectElement && element.value.includes(value)) {
        record(element, 'value');
      }
      for (const node of Array.from(element.childNodes)) {
        if (node.nodeType === Node.TEXT_NODE && (node.textContent ?? '').includes(value)) {
          record(element, 'text');
        }
      }
    }
    for (const [label, store] of [
      ['localStorage', window.localStorage],
      ['sessionStorage', window.sessionStorage],
    ] as const) {
      for (let index = 0; index < store.length; index += 1) {
        const key = store.key(index) ?? '';
        if (key.includes(value) || (store.getItem(key) ?? '').includes(value)) {
          outside.add(`${label}[${key}]`);
        }
      }
    }
    if (document.cookie.includes(value)) outside.add('document.cookie');
    if (document.location.href.includes(value)) outside.add('location.href');
    if (JSON.stringify(window.history.state ?? null).includes(value)) outside.add('history.state');
    return { holders: Array.from(holders), outside: Array.from(outside) };
  }, secret);
}

/** Asserts a one-time value appears nowhere outside the reveal slot. */
export async function expectSecretAbsent(
  page: Page,
  secret: string,
  context: string,
): Promise<void> {
  const footprint = await secretFootprint(page, secret);
  expect(footprint.outside, `${context} leaked the one-time value`).toEqual([]);
}

/**
 * Asserts the stronger half of §8 while the reveal is open: the value is held by exactly one
 * element, and it is inside the reveal slot. One holder is the whole point — a second copy in a
 * hidden field, a cache attribute or a second input would make "shown once" false even though
 * nothing leaked *outside* the slot.
 */
export async function expectSecretOnlyInReveal(
  page: Page,
  secret: string,
  context: string,
): Promise<void> {
  const footprint = await secretFootprint(page, secret);
  expect(footprint.outside, `${context} leaked the one-time value`).toEqual([]);
  expect(
    footprint.holders,
    `${context} did not hold the one-time value in exactly one place`,
  ).toHaveLength(1);
}

/** Asserts the reveal slot is closed: no wrapper, no rendered value. */
export async function expectNoReveal(page: Page): Promise<void> {
  await expect(revealSlot(page)).toHaveCount(0);
  await expect(revealValue(page)).toHaveCount(0);
}
