// 09.7.1-F6 (plan 09.7.1 §2 AC2/AC3/AC7, §10 F6): the member role edit in real Chromium, against
// the FIXTURES build.
//
// What this suite can and cannot observe, stated once. The `fixtures` bundle answers the API in
// process — `provideFixtureBackend()` replaces Angular's `HttpBackend`
// (`apps/web/src/app/core/dev-fixtures/fixture-backend.ts:232-236`), so no network request exists
// for Playwright to intercept, hold or count (the same reason `settings-members.qa.spec.ts:1-6`
// sends retry/double-submit to Vitest). A case here therefore proves a *round trip happened* — the
// dialog is `aria-busy` while the store awaits the answer, and the row then shows exactly the role
// set of the corpus document that answered, a value the dialog never holds — and never claims to
// have seen the `Idempotency-Key` header: that is pinned where it is observable, by
// `core/data/members-data.service.spec.ts:375` (grant POST `{ role_id }` + the intent's key) and
// `:422` (revoke DELETE of the one pair URL + the key), with the header's own contract in
// `core/http/idempotency.interceptor.spec.ts`.
//
// The four calls the dialog makes are `getMembership` (the store's re-read after a confirmed
// change), `grantMembershipRole`, `revokeMembershipRole` and the reads the tab already makes. Their
// answers are selected per operation (`?fixture.variant.<operationId>=<variant>`,
// `./support.ts`), because a static corpus cannot be changed by a write: the "after the grant" state
// is its own document (`fixtures/web-api/membership-get-after-grant`), not something the corpus
// derives from a preceding POST.
//
// Every assertion keys on the plan's §10 F6 DOM contract — `app-members-page[data-members-phase]`,
// rows `[data-members-row][data-membership-id]` and their `.role-name` cells,
// `[data-members-edit-open]`, `[data-members-dialog="edit-roles"]`,
// `[data-members-role-option][data-role-id]`, `[data-members-submit]`, `[data-members-error]` +
// `[data-members-error-code]`, `[data-members-last-admin-hint]`,
// `[data-members-applied-summary]` with `[data-members-applied-item]` /
// `[data-members-not-applied-item]` — never on a localized label, so both locale bundles answer the
// same. Refusal text is read from the corpus JSON the case answers with, never retyped.
import { expect, test, type Locator, type Page } from '@playwright/test';

import getAfterGrant from '../../../../fixtures/web-api/membership-get-after-grant/input.json';
import grantForbidden from '../../../../fixtures/web-api/membership-role-grant-forbidden/input.json';
import grantUnknownRole from '../../../../fixtures/web-api/membership-role-grant-unknown-role/input.json';
import revokeLastAdmin from '../../../../fixtures/web-api/membership-role-revoke-last-admin/input.json';
import membershipsList from '../../../../fixtures/web-api/memberships-list/input.json';
import rolesList from '../../../../fixtures/web-api/roles-list/input.json';
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

/** One membership row of `fixtures/web-api/memberships-list`, as the corpus spells it. */
type FixtureMembership = (typeof membershipsList.body.items)[number];

/** Every membership of the corpus page. */
const MEMBERSHIPS: readonly FixtureMembership[] = membershipsList.body.items;

/** The role catalogue the dialog's checkbox group is built from. */
const ROLES = rolesList.body.items;

/**
 * The member the grant cases edit: an active member who is NOT the sole tenant_admin (`qa@acme.test`
 * in the corpus), so the corpus's `membership-get-after-grant` and `membership-role-grant-ok`
 * documents describe exactly this row. Found by status and role code, never by position.
 */
function grantTarget(): FixtureMembership {
  const candidate = MEMBERSHIPS.find(
    (membership) =>
      membership.status === 'active' &&
      !membership.roles.some((role) => role.code === 'tenant_admin'),
  );
  if (candidate === undefined) {
    throw new Error('fixtures/web-api/memberships-list holds no active non-admin member');
  }
  return candidate;
}

const GRANT_TARGET = grantTarget();

/**
 * The role the grant cases add: the built-in `tenant_admin` of the catalogue, which the corpus's
 * `membership-role-grant-ok` / `membership-get-after-grant` documents answer with. Found by code, so
 * a corpus reorder cannot move the case onto another role.
 */
function tenantAdminRole(): (typeof ROLES)[number] {
  const role = ROLES.find((candidate) => candidate.code === 'tenant_admin');
  if (role === undefined) {
    throw new Error('fixtures/web-api/roles-list holds no tenant_admin role');
  }
  return role;
}

const TENANT_ADMIN_ROLE = tenantAdminRole();

/** The one active `tenant_admin` of the corpus page — the row the last-admin guard protects. */
function soleAdmin(): FixtureMembership {
  const admins = MEMBERSHIPS.filter(
    (membership) =>
      membership.status === 'active' &&
      membership.roles.some((role) => role.code === 'tenant_admin'),
  );
  const [only, ...rest] = admins;
  if (only === undefined || rest.length > 0) {
    throw new Error(
      `fixtures/web-api/memberships-list must hold exactly one active tenant_admin, found ${String(admins.length)}`,
    );
  }
  return only;
}

const SOLE_ADMIN = soleAdmin();

/**
 * The role names the sole admin's row shows. The row's `tenant_admin` role is the catalogue's
 * built-in one, so its checkbox is `TENANT_ADMIN_ROLE`'s — the corpus spells both with the same id.
 */
const SOLE_ADMIN_ROLE_NAMES = SOLE_ADMIN.roles.map((role) => role.name);

/** The server's own code and text, taken from the documents the cases answer with. */
const FORBIDDEN_CODE = grantForbidden.body.error.code;
const FORBIDDEN_MESSAGE = grantForbidden.body.error.message;
const UNKNOWN_ROLE_CODE = grantUnknownRole.body.error.code;
const UNKNOWN_ROLE_MESSAGE = grantUnknownRole.body.error.message;
const REVOKE_LAST_ADMIN_CODE = revokeLastAdmin.body.error.code;
const REVOKE_LAST_ADMIN_MESSAGE = revokeLastAdmin.body.error.message;

/** The role set the corpus says the member holds after the grant (`getMembership`'s answer). */
const ROLE_NAMES_BEFORE = GRANT_TARGET.roles.map((role) => role.name);
const ROLE_NAMES_AFTER = getAfterGrant.body.roles.map((role) => role.name);

/**
 * The latency the "nothing is optimistic" case holds every answer with: long enough to observe the
 * page while the grant is in flight, short enough for the case to finish inside the 60 s budget.
 */
const HELD_ANSWER = '?fixture.latency=2000';

/** The rows `[data-members-row]` of the page, in DOM order — one spelling for every case. */
const MEMBER_ROWS = 'app-members-page [data-members-row]';

/** The edit dialog of `app-edit-roles-dialog`, selected by its own `data-members-dialog` value. */
const EDIT_DIALOG = '[data-members-dialog="edit-roles"][data-state="open"]';

/** The tab's own phase hook, awaited before any content claim (loading → ready). */
async function expectMembersReady(page: Page): Promise<void> {
  const host = page.locator('app-members-page');
  await expect(host).toHaveCount(1);
  await expect(host).toHaveAttribute('data-members-phase', 'ready');
}

/** One row of the members table, by membership id. */
function memberRow(page: Page, membershipId: string): Locator {
  return page.locator(`${MEMBER_ROWS}[data-membership-id="${membershipId}"]`);
}

/** The role names a row currently shows, sorted so the case never depends on server order. */
async function rowRoleNames(row: Locator): Promise<string[]> {
  const names = await row.locator('.role-name').allInnerTexts();
  return names.map((name) => name.trim()).sort();
}

/**
 * The rendered refusal: the message paragraph exactly as the server wrote it, plus the code exactly.
 * The message is its own `<p>` and a sibling of the code, so the comparison is scoped to that
 * paragraph — the region also carries the static `last_admin` hint and, after a partial apply, the
 * applied/not-applied summary, and a whole-region comparison would trip over them rather than over
 * the message.
 */
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
      await expect(row.locator('[data-members-edit-open]')).toHaveCount(1);
      // The starting state is the list's own role set: one role, the one the corpus gives it.
      await expect.poll(() => rowRoleNames(row)).toEqual([...ROLE_NAMES_BEFORE].sort());

      const opener = row.locator('[data-members-edit-open]');
      await opener.click();
      const dialog = page.locator(EDIT_DIALOG);
      await expect(dialog).toBeVisible();

      // The checkbox group is the whole role catalogue, and the member's own roles come checked:
      // the dialog is a draft over server data, not a second source of truth.
      await expect(dialog.locator('[data-members-role-option]')).toHaveCount(ROLES.length);
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
      await expect.poll(() => rowRoleNames(row)).toEqual([...ROLE_NAMES_AFTER].sort());
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
      await row.locator('[data-members-edit-open]').click();

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
      await expect.poll(() => rowRoleNames(row)).toEqual([...SOLE_ADMIN_ROLE_NAMES].sort());
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
      await row.locator('[data-members-edit-open]').click();

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
      await expect.poll(() => rowRoleNames(row)).toEqual([...ROLE_NAMES_BEFORE].sort());
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
      await row.locator('[data-members-edit-open]').click();
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
      await expect.poll(() => rowRoleNames(row)).toEqual([...ROLE_NAMES_BEFORE].sort());
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
      await row.locator('[data-members-edit-open]').click();
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
      await expect(applied).toContainText(TENANT_ADMIN_ROLE.name);
      await expect(notApplied).toContainText(revokedRole.name);
      await expect(dialog).toBeVisible();

      // The row is the refetched server row — the granted role is there — never the draft.
      await expect.poll(() => rowRoleNames(row)).toEqual([...ROLE_NAMES_AFTER].sort());
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
    await expect.poll(() => rowRoleNames(row)).toEqual([...ROLE_NAMES_BEFORE].sort());

    await row.locator('[data-members-edit-open]').click();
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
    expect(await rowRoleNames(row)).toEqual([...ROLE_NAMES_BEFORE].sort());
    await expect(row.locator('.role-name')).toHaveCount(GRANT_TARGET.roles.length);

    // Only the answer changes it, and only to what the answer says.
    await expect(dialog).not.toBeVisible({ timeout: 20_000 });
    await expect.poll(() => rowRoleNames(row)).toEqual([...ROLE_NAMES_AFTER].sort());
  });
});

test.describe('the edit dialog is keyboard-operable', () => {
  test('Tab stays inside it, Escape closes it and focus returns to the opener', async ({
    page,
  }) => {
    await page.goto(withVariants(membersPath('en'), [SETTINGS_ADMIN_GRANTS]));
    await expectMembersReady(page);

    const row = memberRow(page, GRANT_TARGET.id);
    const opener = row.locator('[data-members-edit-open]');
    await opener.click();

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
    await opener.click();
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
      await memberRow(page, GRANT_TARGET.id).locator('[data-members-edit-open]').click();
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

    // The mutation column collapses below 768px (`members.page.css:225-232`), so the control is not
    // offered — and the notice says how to reach it, instead of the buttons shrinking into it.
    await expect(page.locator('app-members-page .narrow-notice')).toBeVisible();
    for (const button of await page.locator('app-members-page [data-members-edit-open]').all()) {
      await expect(button).toBeHidden();
    }
    await expect(page.locator('app-members-page [data-members-edit-open]')).toHaveCount(
      MEMBERSHIPS.filter(
        (membership) => membership.status === 'active' || membership.status === 'pending',
      ).length,
    );

    await expectNoA11yViolations(page, 'members edit-roles 360 en/light');
  });
});
