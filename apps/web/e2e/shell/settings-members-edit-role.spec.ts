// The fixtures backend answers in-process. These tests observe the refetched role set;
// service tests cover the HTTP request and its idempotency key.
import { expect, test, type Locator, type Page } from '@playwright/test';
import { MEMBER_ROWS, memberRow, openMemberAction, openMemberMenu } from './member-controls';

import {
  expectedLabel,
  expectedLabels,
  GRANT_TARGET,
  MEMBERSHIPS,
  ROLES,
  ROLES_AFTER,
  SOLE_ADMIN,
  TENANT_ADMIN_ROLE,
} from './member-role-fixtures';
import grantForbidden from '../../../../fixtures/web-api/membership-role-grant-forbidden/input.json';
import grantUnknownRole from '../../../../fixtures/web-api/membership-role-grant-unknown-role/input.json';
import revokeLastAdmin from '../../../../fixtures/web-api/membership-role-revoke-last-admin/input.json';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  LOCALES,
  MEMBERS_GRANT_FORBIDDEN,
  MEMBERS_GRANT_UNKNOWN_ROLE,
  MEMBERS_READONLY_GRANTS,
  MEMBERS_REVOKE_LAST_ADMIN,
  membersPath,
  seedStoredTheme,
  SETTINGS_ADMIN_GRANTS,
  THEMES,
  waitForDialogAnimations,
  withVariants,
} from './support';

/** The server's own code and text, taken from the documents the cases answer with. */
const FORBIDDEN_CODE = grantForbidden.body.error.code;
const FORBIDDEN_MESSAGE = grantForbidden.body.error.message;
const UNKNOWN_ROLE_CODE = grantUnknownRole.body.error.code;
const UNKNOWN_ROLE_MESSAGE = grantUnknownRole.body.error.message;
const REVOKE_LAST_ADMIN_CODE = revokeLastAdmin.body.error.code;
const REVOKE_LAST_ADMIN_MESSAGE = revokeLastAdmin.body.error.message;

const HELD_ANSWER = '?fixture.latency=2000';

/** The edit dialog of `app-edit-roles-dialog`, selected by its own `data-members-dialog` value. */
const EDIT_DIALOG = '[data-members-dialog="edit-roles"][data-state="open"]';

/** The tab's own phase hook, awaited before any content claim (loading → ready). */
async function expectMembersReady(page: Page): Promise<void> {
  const host = page.locator('app-members-page');
  await expect(host).toHaveCount(1);
  await expect(host).toHaveAttribute('data-members-phase', 'ready');
}

/** The role names a row currently shows, sorted so the case never depends on server order. */
async function rowRoleNames(row: Locator): Promise<string[]> {
  const names = await row.locator('.role-name').allInnerTexts();
  return names.map((name) => name.trim()).sort();
}

async function expectRefusal(region: Locator, code: string, message: string): Promise<void> {
  await expect(region).toBeVisible();
  await expect(region.locator('p').first()).toHaveText(message);
  await expect(region.locator('[data-members-error-code]')).toHaveText(code);
}

/** Where a Tab press left focus, relative to the edit dialog. */
interface DialogFocus {
  /** `document.activeElement`'s tag name, `NULL` when nothing is focused. */
  tag: string;
  /** The role checkbox's `data-role-id`, `null` on any other stop. */
  roleId: string | null;
  /** True while focus is inside the dialog — the trap's own question. */
  inside: boolean;
  /** True when focus reached a control of the page the dialog covers — the one thing a modal forbids. */
  behind: boolean;
}

/**
 * Where a Tab press left focus, relative to the edit dialog. `inside` is the trap's own question;
 * `behind` is the one thing a modal dialog must never allow — focus on a control of the page the
 * overlay covers. `roleId` names the focused role checkbox's host, and is `null` on any other stop.
 */
async function focusedIn(page: Page): Promise<DialogFocus> {
  return page.evaluate((selector) => {
    const element = document.activeElement as HTMLElement | null;
    return {
      tag: element?.tagName ?? 'NULL',
      roleId: element?.closest('[data-members-role-option]')?.getAttribute('data-role-id') ?? null,
      inside: element !== null && element.closest(selector) !== null,
      behind:
        element !== null &&
        element.closest('app-members-page') !== null &&
        element.closest(selector) === null,
    };
  }, EDIT_DIALOG);
}

test.describe('a role change is the server’s answer, never a local guess', () => {
  for (const locale of LOCALES) {
    test(`granting a role saves, closes the dialog and shows the server's role set (${locale})`, async ({
      page,
    }) => {
      await page.goto(withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]));
      await expectMembersReady(page);

      const row = memberRow(page, GRANT_TARGET.id);
      await openMemberMenu(page, row);
      await expect(page.locator('[data-members-edit-open]')).toHaveCount(1);
      await page.keyboard.press('Escape');
      // The starting state is the list's own role set: one role, the one the corpus gives it.
      await expect
        .poll(() => rowRoleNames(row))
        .toEqual(expectedLabels(GRANT_TARGET.roles, locale));

      const opener = await openMemberAction(page, row, 'edit-open');
      const dialog = page.locator(EDIT_DIALOG);
      await expect(dialog).toBeVisible();

      // The checkbox group is the whole role catalogue, and the member's own roles come checked:
      // the dialog is a draft over server data, not a second source of truth.
      await expect(dialog.locator('[data-members-role-option]')).toHaveCount(ROLES.length);
      for (const role of ROLES) {
        const label = expectedLabel(role, locale);
        await expect(dialog.locator(`[data-role-id="${role.id}"] button`)).toHaveAccessibleName(
          label,
        );
        await expect(page.locator(`[id="matrix-role-${role.id}"]`)).toHaveText(label);
      }
      const granted = dialog.locator(
        `[data-members-role-option][data-role-id="${TENANT_ADMIN_ROLE.id}"] button[role="checkbox"]`,
      );
      await expect(granted).not.toBeChecked();

      const submit = dialog.locator('[data-members-submit]');
      await expect(submit).toBeDisabled();

      // The custom checkbox renders aria-checked on Angular's next render.
      await granted.click();
      await expect(granted).toBeChecked();
      await expect(submit).toBeEnabled();
      await submit.click();
      await expect(dialog).not.toBeVisible();

      // Focus goes back to the control that opened the dialog.
      await expect(opener).toBeFocused();
      // The row then shows exactly the role set of the document that answered `getMembership` — a
      // value no draft on the page holds — and the page announces the change.
      await expect.poll(() => rowRoleNames(row)).toEqual(expectedLabels(ROLES_AFTER, locale));
      await expect(page.locator('app-members-page > p[role="status"]')).toHaveText(/\S/);
    });
  }
});

test.describe('the last admin cannot be demoted: the server refuses in its own words', () => {
  for (const locale of LOCALES) {
    test(`revoking tenant_admin from the sole admin shows the 409 last_admin (${locale})`, async ({
      page,
    }) => {
      const adminPath = withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]);
      await page.goto(withVariants(adminPath, [MEMBERS_REVOKE_LAST_ADMIN]));
      await expectMembersReady(page);

      const row = memberRow(page, SOLE_ADMIN.id);
      await openMemberAction(page, row, 'edit-open');

      const dialog = page.locator(EDIT_DIALOG);
      await expect(dialog).toBeVisible();
      const option = dialog.locator(
        `[data-members-role-option][data-role-id="${TENANT_ADMIN_ROLE.id}"] button[role="checkbox"]`,
      );
      await expect(option).toBeChecked();
      await option.click();
      await expect(option).not.toBeChecked();
      await dialog.locator('[data-members-submit]').click();

      await expectRefusal(
        dialog.locator('[data-members-error]'),
        REVOKE_LAST_ADMIN_CODE,
        REVOKE_LAST_ADMIN_MESSAGE,
      );
      // The static explanation is the dialog's own addition to the verbatim message, and it is the
      // only per-code text the client owns.
      await expect(dialog.locator('[data-members-last-admin-hint]')).toBeVisible();
      // Refused, not partly applied: no confirmed change, so no applied/not-applied summary.
      await expect(dialog.locator('[data-members-applied-summary]')).toHaveCount(0);

      // Still open, still holding the attempt, and the row is untouched: no client-side pre-check
      // and no optimistic demotion.
      await expect(dialog).toBeVisible();
      await expect.poll(() => rowRoleNames(row)).toEqual(expectedLabels(SOLE_ADMIN.roles, locale));
    });
  }
});

test.describe('a refused grant is the server’s own message, never the client’s guess', () => {
  for (const locale of LOCALES) {
    test(`a refused grant renders the 403 forbidden and leaves the roles alone (${locale})`, async ({
      page,
    }) => {
      const adminPath = withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]);
      await page.goto(withVariants(adminPath, [MEMBERS_GRANT_FORBIDDEN]));
      await expectMembersReady(page);

      const row = memberRow(page, GRANT_TARGET.id);
      await openMemberAction(page, row, 'edit-open');

      const dialog = page.locator(EDIT_DIALOG);
      await expect(dialog).toBeVisible();
      const granted = dialog.locator(
        `[data-members-role-option][data-role-id="${TENANT_ADMIN_ROLE.id}"] button[role="checkbox"]`,
      );
      await granted.click();
      await expect(granted).toBeChecked();
      await dialog.locator('[data-members-submit]').click();

      await expectRefusal(
        dialog.locator('[data-members-error]'),
        FORBIDDEN_CODE,
        FORBIDDEN_MESSAGE,
      );
      // The last-admin hint belongs to that code alone: a forbidden answer must not borrow it.
      await expect(dialog.locator('[data-members-last-admin-hint]')).toHaveCount(0);
      await expect(dialog.locator('[data-members-applied-summary]')).toHaveCount(0);
      await expect(dialog).toBeVisible();
      await expect
        .poll(() => rowRoleNames(row))
        .toEqual(expectedLabels(GRANT_TARGET.roles, locale));
    });
  }

  for (const locale of LOCALES) {
    test(`a grant the catalogue cannot answer is the 409 unknown_role (${locale})`, async ({
      page,
    }) => {
      // The catalogue the dialog drew its checkbox from and the role the write path accepts can
      // drift; the server's 409 is the only thing that may report it.
      const adminPath = withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]);
      await page.goto(withVariants(adminPath, [MEMBERS_GRANT_UNKNOWN_ROLE]));
      await expectMembersReady(page);

      const row = memberRow(page, GRANT_TARGET.id);
      await openMemberAction(page, row, 'edit-open');
      const dialog = page.locator(EDIT_DIALOG);
      await expect(dialog).toBeVisible();
      const granted = dialog.locator(
        `[data-members-role-option][data-role-id="${TENANT_ADMIN_ROLE.id}"] button[role="checkbox"]`,
      );
      await granted.click();
      await expect(granted).toBeChecked();
      await dialog.locator('[data-members-submit]').click();

      await expectRefusal(
        dialog.locator('[data-members-error]'),
        UNKNOWN_ROLE_CODE,
        UNKNOWN_ROLE_MESSAGE,
      );
      await expect(dialog.locator('[data-members-last-admin-hint]')).toHaveCount(0);
      await expect(dialog.locator('[data-members-applied-summary]')).toHaveCount(0);
      await expect(dialog).toBeVisible();
      await expect
        .poll(() => rowRoleNames(row))
        .toEqual(expectedLabels(GRANT_TARGET.roles, locale));
    });
  }
});

test.describe('a granted change followed by a refused one is reported as both', () => {
  for (const locale of LOCALES) {
    test(`the applied grant and the refused revoke are listed, and the row is the server's (${locale})`, async ({
      page,
    }) => {
      // Two answers at once: the grant succeeds (the corpus's default), the revoke is refused.
      const adminPath = withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]);
      await page.goto(withVariants(adminPath, [MEMBERS_REVOKE_LAST_ADMIN]));
      await expectMembersReady(page);

      const row = memberRow(page, GRANT_TARGET.id);
      await openMemberAction(page, row, 'edit-open');
      const dialog = page.locator(EDIT_DIALOG);
      await expect(dialog).toBeVisible();

      // Grants are sent before revokes, so this draft is one confirmed change then one refusal.
      const granted = dialog.locator(
        `[data-members-role-option][data-role-id="${TENANT_ADMIN_ROLE.id}"] button[role="checkbox"]`,
      );
      await granted.click();
      await expect(granted).toBeChecked();
      const revokedRole = GRANT_TARGET.roles[0];
      if (revokedRole === undefined) {
        throw new Error('the corpus grant target holds no role to revoke');
      }
      const revoked = dialog.locator(
        `[data-members-role-option][data-role-id="${revokedRole.id}"] button[role="checkbox"]`,
      );
      await expect(revoked).toBeChecked();
      await revoked.click();
      await expect(revoked).not.toBeChecked();

      await dialog.locator('[data-members-submit]').click();

      await expectRefusal(
        dialog.locator('[data-members-error]'),
        REVOKE_LAST_ADMIN_CODE,
        REVOKE_LAST_ADMIN_MESSAGE,
      );
      await expect(dialog.locator('[data-members-last-admin-hint]')).toBeVisible();

      // The partial outcome is explicit: exactly the grant is listed as applied, exactly the revoke
      // as not applied, and the dialog stays open on the not-applied work.
      const summary = dialog.locator('[data-members-applied-summary]');
      await expect(summary).toBeVisible();
      const applied = summary.locator('[data-members-applied-item]');
      const notApplied = summary.locator('[data-members-not-applied-item]');
      await expect(applied).toHaveCount(1);
      await expect(notApplied).toHaveCount(1);
      await expect(applied).toContainText(expectedLabel(TENANT_ADMIN_ROLE, locale));
      await expect(notApplied).toContainText(expectedLabel(revokedRole, locale));
      await expect(dialog).toBeVisible();

      // The row is the refetched server row — the granted role is there — never the draft.
      await expect.poll(() => rowRoleNames(row)).toEqual(expectedLabels(ROLES_AFTER, locale));
      // No page-level "saved" announcement: the dialog, not the page, owns the partial outcome and
      // is still open on the work that was not applied.
      await expect(page.locator('app-members-page > p[role="status"]')).toHaveText('');
    });
  }
});

test.describe('a membership:read session is offered no role edit at all', () => {
  for (const locale of LOCALES) {
    test(`no edit control and no dialog exist without membership:write (${locale})`, async ({
      page,
    }) => {
      await page.goto(withVariants(membersPath(locale), [MEMBERS_READONLY_GRANTS]));
      await expectMembersReady(page);

      // The tab stays readable — this is a refusal to offer the action, not to show the screen.
      await expect(page.locator(MEMBER_ROWS)).toHaveCount(MEMBERSHIPS.length);
      // ABSENT from the DOM, not disabled: a disabled button would still confirm the action exists.
      await expect(page.locator('app-members-page [data-members-edit-open]')).toHaveCount(0);
      await expect(page.locator('app-members-page [data-members-actions]')).toHaveCount(0);
      await expect(page.locator('[data-members-dialog="edit-roles"]')).toHaveCount(0);
    });
  }
});

test.describe('nothing on the row changes before the server answers', () => {
  test('the old role set stays while the grant is in flight', async ({ page }) => {
    const adminPath = withVariants(membersPath('en'), [SETTINGS_ADMIN_GRANTS]);
    await page.goto(withVariants(adminPath, [HELD_ANSWER]));
    // The fixture delay also holds the sequential session and route reads before the tab exists.
    await expect(page.locator('app-members-page')).toHaveAttribute('data-members-phase', 'ready', {
      timeout: 20_000,
    });

    const row = memberRow(page, GRANT_TARGET.id);
    await expect.poll(() => rowRoleNames(row)).toEqual(expectedLabels(GRANT_TARGET.roles, 'en'));

    await openMemberAction(page, row, 'edit-open');
    const dialog = page.locator(EDIT_DIALOG);
    await expect(dialog).toBeVisible();
    const granted = dialog.locator(
      `[data-members-role-option][data-role-id="${TENANT_ADMIN_ROLE.id}"] button[role="checkbox"]`,
    );
    await granted.click();
    await expect(granted).toBeChecked();
    await dialog.locator('[data-members-submit]').click();

    // Held: the dialog is busy on the answer, and the row behind it is still the old role set —
    // the draft lives in the dialog and nowhere else.
    await expect(dialog).toHaveAttribute('aria-busy', 'true');
    expect(await rowRoleNames(row)).toEqual(expectedLabels(GRANT_TARGET.roles, 'en'));
    await expect(row.locator('.role-name')).toHaveCount(GRANT_TARGET.roles.length);

    // Only the answer changes it, and only to what the answer says.
    await expect(dialog).not.toBeVisible({ timeout: 20_000 });
    await expect.poll(() => rowRoleNames(row)).toEqual(expectedLabels(ROLES_AFTER, 'en'));
  });
});

test.describe('the edit dialog is keyboard-operable', () => {
  test('Tab stays inside it, Escape closes it and focus returns to the opener', async ({
    page,
  }) => {
    await page.goto(withVariants(membersPath('en'), [SETTINGS_ADMIN_GRANTS]));
    await expectMembersReady(page);

    const row = memberRow(page, GRANT_TARGET.id);
    const opener = await openMemberAction(page, row, 'edit-open', true);

    const dialog = page.locator(EDIT_DIALOG);
    await expect(dialog).toBeVisible();
    // Opening put focus on the first role checkbox, not on the page behind the dialog.
    await expect(
      dialog.locator('[data-members-role-option] button[role="checkbox"]').first(),
    ).toBeFocused();

    // The CDK focus trap keeps both edges inside the overlay: every role checkbox, then Cancel
    // (Save is disabled on an unchanged draft). Walk past both edges and require every stop to
    // remain inside, with the same controls visited in both directions.
    const step = async (key: 'Tab' | 'Shift+Tab'): Promise<DialogFocus> => {
      await page.keyboard.press(key);
      return focusedIn(page);
    };

    // One more than the dialog's own stops (nine checkboxes, then Cancel), so the edge is crossed.
    const forward: DialogFocus[] = [];
    for (let press = 0; press < ROLES.length + 2; press += 1) forward.push(await step('Tab'));
    const steps = forward.map((focus, index) => `${String(index + 1)}:${focus.tag}`).join(' ');
    expect(
      forward.filter((focus) => focus.behind),
      `a press reached the page behind: ${steps}`,
    ).toEqual([]);
    expect(
      forward.filter((focus) => !focus.inside),
      `a press left the dialog: ${steps}`,
    ).toEqual([]);

    // The order itself: every following checkbox in catalogue order, then the footer button, then
    // the wrap back to the first checkbox, then the second.
    expect(forward.slice(0, ROLES.length - 1).map((focus) => focus.roleId)).toEqual(
      ROLES.slice(1).map((role) => role.id),
    );
    const [beforeLast, last, wrapped, afterWrap] = forward.slice(ROLES.length - 2);
    expect(last?.roleId).toBe(null);
    expect(last?.tag).toBe('BUTTON');
    expect(beforeLast?.roleId).toBe(ROLES[ROLES.length - 1]?.id);
    expect(wrapped?.roleId, 'Tab at the last control did not wrap to the first').toBe(ROLES[0]?.id);
    expect(afterWrap?.roleId).toBe(ROLES[1]?.id);

    // Shift+Tab wraps the same way in reverse: back to the first checkbox, then Cancel, then the
    // last checkbox again — never out of the dialog.
    const backward = [await step('Shift+Tab'), await step('Shift+Tab'), await step('Shift+Tab')];
    const backSteps = backward.map((focus, index) => `${String(index + 1)}:${focus.tag}`).join(' ');
    expect(
      backward.filter((focus) => focus.behind),
      `a press reached the page behind: ${backSteps}`,
    ).toEqual([]);
    expect(
      backward.filter((focus) => !focus.inside),
      `a press left the dialog: ${backSteps}`,
    ).toEqual([]);
    expect(backward.map((focus) => focus.roleId)).toEqual([
      ROLES[0]?.id,
      null,
      ROLES[ROLES.length - 1]?.id,
    ]);
    expect(backward[1]?.tag).toBe('BUTTON');

    await page.keyboard.press('Escape');
    await expect(dialog).not.toBeVisible();
    await expect(opener).toBeFocused();
    // Escape is a cancel: the dialog reopened on a fresh draft, not on the abandoned one.
    await openMemberAction(page, row, 'edit-open', true);
    await expect(dialog).toBeVisible();
    await expect(
      dialog.locator(
        `[data-members-role-option][data-role-id="${TENANT_ADMIN_ROLE.id}"] button[role="checkbox"]`,
      ),
    ).not.toBeChecked();
  });
});

test.describe('the edit dialog is WCAG 2.2 AA clean and not offered on a narrow screen', () => {
  for (const theme of THEMES) {
    test(`dialog open, en bundle, ${theme} tokens`, async ({ page }) => {
      await seedStoredTheme(page, theme);
      await page.goto(withVariants(membersPath('en'), [SETTINGS_ADMIN_GRANTS]));
      await expectMembersReady(page);
      await openMemberAction(page, memberRow(page, GRANT_TARGET.id), 'edit-open');
      await expect(page.locator(EDIT_DIALOG)).toBeVisible();
      await waitForDialogAnimations(page, EDIT_DIALOG);

      // The dialog is overlaid content, so it is scanned over the page it covers: a modal that is
      // clean alone and broken over the table would otherwise pass.
      await expectNoA11yViolations(page, `members edit-roles dialog en/${theme}`);
    });
  }

  test('at 360px the narrow notice replaces the action and the page stays clean', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 360, height: 800 });
    await page.goto(withVariants(membersPath('en'), [SETTINGS_ADMIN_GRANTS]));
    await expectMembersReady(page);

    // Narrow records keep every membership and omit mutations rather than shrinking the controls.
    await expect(page.locator('app-members-page .narrow-notice')).toBeVisible();
    await expect(page.locator(MEMBER_ROWS)).toHaveCount(MEMBERSHIPS.length);
    for (const membership of MEMBERSHIPS) {
      await expect(memberRow(page, membership.id)).toBeVisible();
    }
    await expect(
      page.locator('app-members-page [data-members-actions], [data-members-edit-open]'),
    ).toHaveCount(0);

    await expectNoA11yViolations(page, 'members edit-roles 360 en/light');
  });
});
