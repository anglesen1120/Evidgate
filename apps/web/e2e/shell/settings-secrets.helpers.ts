import { expect, type Locator, type Page } from '@playwright/test';
import rotation from '../../../../fixtures/web-api/ingest-token-rotate/input.json';
import references from '../../../../fixtures/web-api/ingest-tokens-list-settings/input.json';
import projects from '../../../../fixtures/web-api/projects-list/input.json';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  PROJECT_STOREFRONT,
  SECRETS_LIST,
  SECRETS_WRITE_GRANTS,
  secretsReviewPath,
  withVariants,
  waitForDialogAnimations,
  type ShellLocale,
} from './support';

export const PAGE = 'app-secrets-page';
export const ROWS = `${PAGE} [data-secret-reference]`;
export const DIALOG = '[role="dialog"]';
export const SECRET = rotation.body.secret;
const TARGET = ((): (typeof references.body.items)[number] => {
  const first = references.body.items[0];
  if (first === undefined) {
    throw new Error('fixtures/web-api/ingest-tokens-list-settings holds no reference');
  }
  return first;
})();

/** No plaintext in any template sink, control property, address or persistent browser store. */
export async function expectNoPlaintext(page: Page): Promise<void> {
  const sinks = await page.evaluate(() => ({
    html: document.documentElement.innerHTML,
    controls: Array.from(document.querySelectorAll('input, textarea')).map(
      (element) => (element as HTMLInputElement | HTMLTextAreaElement).value,
    ),
    url: location.href,
    local: JSON.stringify(Object.entries(localStorage)),
    session: JSON.stringify(Object.entries(sessionStorage)),
  }));
  for (const [sink, value] of Object.entries(sinks)) {
    const text = JSON.stringify(value);
    expect(text, `${sink} must not retain the one-time credential`).not.toContain(SECRET);
    expect(text, `${sink} must not contain any fixture plaintext`).not.toMatch(
      /(?:egt|evgi)_FIXTURE_ONLY_/,
    );
  }
}

export function rotateButton(page: Page): Locator {
  return page.locator(`${PAGE} [data-rotate][data-reference-id="${TARGET.id}"]`);
}

export function projectName(id: string): string {
  const project = projects.body.items.find((candidate) => candidate.id === id);
  if (project === undefined) throw new Error(`The fixture has no project ${id}`);
  return project.name;
}

export async function rotationConfirmation(page: Page): Promise<Locator> {
  const dialog = page.getByRole('alertdialog');
  await expect(dialog).toBeVisible();
  await expect(dialog).toHaveAccessibleName(/\S/u);
  await expect(dialog).toHaveAccessibleDescription(/\S/u);
  await expect(dialog.locator('dd').nth(0)).toHaveText(TARGET.name);
  await expect(dialog.locator('dd').nth(1)).toHaveText(projectName(PROJECT_STOREFRONT));
  await expect(dialog.locator('dd').nth(2).locator('code')).toHaveText(TARGET.prefix);
  await expect(dialog.locator('dd').nth(3).locator('code')).toHaveText(TARGET.scopes);
  await expect(page.getByTestId('reveal-value')).toHaveCount(0);
  await expectNoPlaintext(page);
  await waitForDialogAnimations(page, '[role="alertdialog"]');
  await expect(dialog.locator('[data-rotation-cancel]')).toBeFocused();
  return dialog;
}

export async function approveRotation(page: Page): Promise<void> {
  const confirmation = await rotationConfirmation(page);
  await confirmation.locator('[data-rotation-confirm-submit]').click();
  await expect(confirmation).not.toBeVisible();
}

export async function ready(page: Page): Promise<void> {
  await expect(page.locator(PAGE)).toBeVisible();
  await expect(page.locator(ROWS)).toHaveCount(references.body.items.length);
  await expect(page.locator('#secrets-project')).toHaveRole('combobox');
  await expect(page.locator('#secrets-project')).toHaveText(projectName(PROJECT_STOREFRONT));
  await expect(page.locator('#secrets-project-value')).toHaveText(projectName(PROJECT_STOREFRONT));
}

export async function visit(
  page: Page,
  locale: ShellLocale,
  grants = SECRETS_WRITE_GRANTS,
  variants: readonly string[] = [],
): Promise<void> {
  await page.clock.setFixedTime(new Date('2026-10-25T12:00:00Z'));
  await page.goto(withVariants(secretsReviewPath(locale), [grants, SECRETS_LIST, ...variants]));
  await ready(page);
}

export async function reveal(page: Page, confirmationA11yLabel?: string): Promise<Locator> {
  await expectNoPlaintext(page);
  await rotateButton(page).click();
  const confirmation = await rotationConfirmation(page);
  if (confirmationA11yLabel !== undefined) {
    await expectNoA11yViolations(page, confirmationA11yLabel);
  }
  await confirmation.locator('[data-rotation-confirm-submit]').click();
  await expect(confirmation).not.toBeVisible();
  const dialog = page.locator(DIALOG);
  await expect(dialog).toBeVisible();
  await expect(dialog.getByTestId('reveal-value')).toHaveValue(SECRET);
  await waitForDialogAnimations(page, DIALOG);
  // Plaintext belongs to exactly one live reveal control, never to the metadata list or storage.
  await expect(page.getByTestId('reveal-value')).toHaveCount(1);
  await expect(page.locator('app-secret-reference-list')).not.toContainText(SECRET);
  expect(new URL(page.url()).search).not.toContain(SECRET);
  expect(
    await page.evaluate(() =>
      JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]),
    ),
  ).not.toContain(SECRET);
  return dialog;
}
