// 09.7.1-T2.4 (plan 09.7.1 §2 AC2, §5 members row, §6 Membership/Invite rows, §10 T2.2; §8 S2):
// the Members and roles tab in real Chromium, against the FIXTURES build.
//
// AC2 is the whole point of the suite: removing the org's last `tenant_admin` is refused BY THE
// SERVER, and the UI shows that refusal verbatim — so the case submits the real dialog against the
// corpus's 409 `last_admin` document and compares what the page renders with the message that
// document carries (read from `fixtures/web-api/membership-remove-last-admin/input.json`, never
// retyped: a paraphrased copy would pass while the product invented its own wording). The dialog
// must stay open and the row must stay on the page: no client-side pre-check, no optimistic
// removal.
//
// Every assertion keys on the tab's DOM contract (plan §10 T2) — `app-members-page[
// data-members-phase]`, `[data-members-row][data-membership-id]`, `[data-members-matrix]`,
// `[data-members-invite-open]`, `[data-members-remove]`, `[data-members-dialog="invite|remove"]`,
// `[data-members-submit]`, `[data-members-error]` + `[data-members-error-code]` and
// `[data-role-preview]` — never on a localized label, so both locale bundles answer the same.
//
// The session's grants come from corpus variants (`permissions-list-settings-*`), never from a role
// name: the client derives them from the `tenant_assignable` catalogue rows alone
// (`core/session/effective-permissions.source.ts`), and `membership:write` alone decides whether the
// invite and remove controls exist at all.
import { expect, test, type Locator, type Page } from '@playwright/test';

import createInviteExists from '../../../../fixtures/web-api/membership-create-invite-exists/input.json';
import membershipCreateOk from '../../../../fixtures/web-api/membership-create-ok/input.json';
import removeLastAdmin from '../../../../fixtures/web-api/membership-remove-last-admin/input.json';
import removeInvalidTransition from '../../../../fixtures/web-api/membership-remove-invalid-transition/input.json';
import membershipsList from '../../../../fixtures/web-api/memberships-list/input.json';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  expectIndistinguishableNotFound,
  LOCALES,
  MEMBERS_EMPTY_LIST,
  MEMBERS_INVITE_EXISTS,
  MEMBERS_READONLY_GRANTS,
  MEMBERS_REMOVE_INVALID_TRANSITION,
  MEMBERS_REMOVE_LAST_ADMIN,
  membersPath,
  SETTINGS_ADMIN_GRANTS,
  SETTINGS_NO_TAB_GRANTS,
  seedStoredTheme,
  settingsTabPath,
  THEMES,
  waitForDialogAnimations,
  withVariants,
} from './support';

/** One membership row of `fixtures/web-api/memberships-list`, as the corpus spells it. */
type FixtureMembership = (typeof membershipsList.body.items)[number];

/**
 * Every membership of the corpus page. The fixture is the record of what the tab renders, so the
 * counts below are read from it rather than repeated: a row added to the corpus must not silently
 * turn a claim into a tautology.
 */
const MEMBERSHIPS: readonly FixtureMembership[] = membershipsList.body.items;

/**
 * The one active `tenant_admin` of the corpus (`owner@acme.test`) — the row AC2's last-admin guard
 * protects. Found by status and role code, not by position, so a corpus reorder cannot quietly move
 * the case onto another member; a second active admin would make the case prove nothing, hence the
 * exact-count check.
 */
function soleAdminMembership(): FixtureMembership {
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

const SOLE_ADMIN = soleAdminMembership();

/** The membership whose status is `removed`: its Remove control must not exist (02.1.2 §8). */
function removedMembership(): FixtureMembership {
  const removed = MEMBERSHIPS.find((membership) => membership.status === 'removed');
  if (removed === undefined) {
    throw new Error('fixtures/web-api/memberships-list holds no removed membership');
  }
  return removed;
}

/** The address of the membership that is still `pending` — the one a second invite collides with. */
function pendingInviteEmail(): string {
  const pending = MEMBERSHIPS.find((membership) => membership.status === 'pending');
  if (pending === undefined) {
    throw new Error('fixtures/web-api/memberships-list holds no pending invitation');
  }
  return pending.invited_email;
}

/** The `tenant_admin` role id of the corpus rows: the built-in role the invite dialog offers. */
function tenantAdminRoleId(): string {
  const role = SOLE_ADMIN.roles.find((candidate) => candidate.code === 'tenant_admin');
  if (role === undefined) {
    throw new Error('the sole active tenant_admin of memberships-list carries no such role');
  }
  return role.id;
}

/** The server's own text and code, taken from the corpus documents the cases answer with. */
const LAST_ADMIN_MESSAGE = removeLastAdmin.body.error.message;
const LAST_ADMIN_CODE = removeLastAdmin.body.error.code;
const INVITE_EXISTS_MESSAGE = createInviteExists.body.error.message;
const INVITE_EXISTS_CODE = createInviteExists.body.error.code;
const REMOVE_INVALID_TRANSITION_MESSAGE = removeInvalidTransition.body.error.message;
const REMOVE_INVALID_TRANSITION_CODE = removeInvalidTransition.body.error.code;

/** The address typed into the invite dialog on the happy path, bound to the answer's own body. */
const INVITED_EMAIL = membershipCreateOk.body.invited_email;

/** The role whose scope preview the invite case opens. */
const TENANT_ADMIN_ROLE_ID = tenantAdminRoleId();

/** The rows `[data-members-row]` of the page, in DOM order — one spelling for every case. */
const MEMBER_ROWS = 'app-members-page [data-members-row]';

/** The tab's own phase hook, awaited before any content claim (loading → ready). */
async function expectMembersReady(page: Page): Promise<void> {
  const host = page.locator('app-members-page');
  await expect(host).toHaveCount(1);
  await expect(host).toHaveAttribute('data-members-phase', 'ready');
}

/**
 * The rendered mutation error, compared with the server's own `message` as one whole string: the
 * region also carries the code in `[data-members-error-code]`, so the code is removed first and
 * what remains must equal the corpus text exactly — a paraphrase changes it.
 */
async function expectServerMessage(region: Locator, code: string, message: string): Promise<void> {
  await expect(region).toBeVisible();
  await expect(region.locator('[data-members-error-code]')).toHaveText(code);
  expect((await region.innerText()).replace(code, '').trim()).toBe(message);
}

test.describe('AC2: the last-admin refusal is the server text, never a client pre-check', () => {
  for (const locale of LOCALES) {
    test(`removing the sole tenant_admin shows the 409 last_admin verbatim (${locale})`, async ({
      page,
    }) => {
      const adminPath = withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]);
      await page.goto(withVariants(adminPath, [MEMBERS_REMOVE_LAST_ADMIN]));
      await expectMembersReady(page);
      // The page renders the corpus page whole: the count is the fixture's, not a sample.
      await expect(page.locator(MEMBER_ROWS)).toHaveCount(MEMBERSHIPS.length);

      const row = page.locator(`${MEMBER_ROWS}[data-membership-id="${SOLE_ADMIN.id}"]`);
      await expect(row).toHaveCount(1);
      await expect(row.locator('[data-members-remove]')).toHaveCount(1);
      await row.locator('[data-members-remove]').click();

      const dialog = page.locator('[data-members-dialog="remove"][data-state="open"]');
      await expect(dialog).toBeVisible();
      await dialog.locator('[data-members-submit]').click();

      await expectServerMessage(
        dialog.locator('[data-members-error]'),
        LAST_ADMIN_CODE,
        LAST_ADMIN_MESSAGE,
      );

      // Refused, not optimistically applied: the dialog stays open and the guarded row stays put.
      await expect(dialog).toBeVisible();
      await expect(row).toHaveCount(1);
      await expect(row).toHaveAttribute('data-members-status', 'active');
      await expect(page.locator(MEMBER_ROWS)).toHaveCount(MEMBERSHIPS.length);
      await expectMembersReady(page);
    });
  }

  for (const locale of LOCALES) {
    test(`a removed membership offers no Remove control (${locale})`, async ({ page }) => {
      // The admin grant set, so the boundary is real: every active row DOES carry the control and
      // the removed row does not — a page that rendered no control at all would otherwise pass.
      await page.goto(withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]));
      await expectMembersReady(page);

      const removable = MEMBERSHIPS.filter((membership) => membership.status !== 'removed');
      const removeControls = page.locator('app-members-page [data-members-remove]');
      await expect(removeControls).toHaveCount(removable.length);

      const removedId = removedMembership().id;
      const removed = page.locator(`${MEMBER_ROWS}[data-membership-id="${removedId}"]`);
      await expect(removed).toHaveCount(1);
      await expect(removed).toHaveAttribute('data-members-status', 'removed');
      // The row is audit history (02.1.2 §8): it is listed, and its action cell stays empty.
      await expect(removed.locator('[data-members-remove]')).toHaveCount(0);
    });
  }
});

test.describe('removal: the 204 closes the dialog and announces the change', () => {
  for (const locale of LOCALES) {
    test(`removing an active member closes the dialog (${locale})`, async ({ page }) => {
      await page.goto(withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]));
      await expectMembersReady(page);

      // An ACTIVE member who is not the sole admin: the transition the server accepts. A `pending`
      // invite would be 409 `invalid_transition`, so it is not a substitute here.
      const target = MEMBERSHIPS.find(
        (membership) => membership.status === 'active' && membership.id !== SOLE_ADMIN.id,
      );
      if (target === undefined) {
        throw new Error('fixtures/web-api/memberships-list holds no other active member');
      }

      const row = page.locator(`${MEMBER_ROWS}[data-membership-id="${target.id}"]`);
      await row.locator('[data-members-remove]').click();
      const dialog = page.locator('[data-members-dialog="remove"][data-state="open"]');
      await expect(dialog).toBeVisible();
      await dialog.locator('[data-members-submit]').click();

      // 204 (the corpus's bodyless `membership-remove-ok`): success closes the dialog and says so,
      // and no error region was ever produced.
      await expect(dialog).not.toBeVisible();
      await expect(dialog.locator('[data-members-error]')).toHaveCount(0);
      await expect(page.locator('app-members-page > p[role="status"]')).toHaveText(/\S/);
    });
  }
});

test.describe('a membership:read session never sees an action it may not use', () => {
  for (const locale of LOCALES) {
    test(`a grant set without membership:write renders no write control (${locale})`, async ({
      page,
    }) => {
      await page.goto(withVariants(membersPath(locale), [MEMBERS_READONLY_GRANTS]));
      await expectMembersReady(page);

      // The tab itself is readable, matrix included — this is a refusal to offer the actions, not a
      // refusal to show the screen.
      await expect(page.locator(MEMBER_ROWS)).toHaveCount(MEMBERSHIPS.length);
      await expect(page.locator('app-members-page [data-members-matrix]')).toHaveCount(1);

      // ABSENT from the DOM, not disabled: a disabled button would still confirm the action exists
      // (00 §5.6 W5, the same rule the tab strip follows).
      await expect(page.locator('app-members-page [data-members-invite-open]')).toHaveCount(0);
      await expect(page.locator('app-members-page [data-members-remove]')).toHaveCount(0);
      await expect(page.locator('[data-members-dialog]')).toHaveCount(0);
      await expect(page.locator('app-members-page [data-role-preview]')).toHaveCount(0);
    });
  }
});

test.describe('invite: the 201 reveals the token then closes, the 409 invite_exists speaks for itself', () => {
  for (const locale of LOCALES) {
    test(`a successful invite reveals its token, closes on dismiss and announces the change (${locale})`, async ({
      page,
    }) => {
      await page.goto(withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]));
      await expectMembersReady(page);

      const opener = page.locator('app-members-page [data-members-invite-open]');
      await expect(opener).toBeVisible();
      await opener.click();

      const dialog = page.locator('[data-members-dialog="invite"][data-state="open"]');
      await expect(dialog).toBeVisible();
      // The address field is the dialog's own email input; its type is the product's contract and
      // no localized label is consulted.
      await dialog.locator('input[type="email"]').fill(INVITED_EMAIL);

      // The role scope preview is part of the invite path: selecting a built-in role names what it
      // grants, joined from the contract's role grants and the live catalogue (T2.2).
      const roleSelector = `[data-members-role-option][data-role-id="${TENANT_ADMIN_ROLE_ID}"] button[role="checkbox"]`;
      await dialog.locator(roleSelector).click();
      await expect(dialog.locator(roleSelector)).toBeChecked();
      await expect(dialog.locator('[data-role-preview]')).toHaveAttribute(
        'data-preview-kind',
        'ready',
      );

      await dialog.locator('[data-members-submit]').click();

      // 201 (09.7.1-F12): the dialog turns into the one-time reveal of the raw invite token; the
      // dialog closes only when the admin dismisses it, and the page then announces the change in
      // its own status region. The reveal itself is covered by `settings-members-invite.spec.ts`.
      await expect(dialog.getByTestId('reveal-value')).toBeVisible();
      await dialog.getByTestId('reveal-dismiss').click();
      await expect(dialog).not.toBeVisible();
      await expect(page.locator('app-members-page > p[role="status"]')).toHaveText(/\S/);
      await expect(page.locator(MEMBER_ROWS)).toHaveCount(MEMBERSHIPS.length);
    });
  }

  for (const locale of LOCALES) {
    test(`a duplicate invite shows the 409 invite_exists verbatim (${locale})`, async ({
      page,
    }) => {
      const adminPath = withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]);
      await page.goto(withVariants(adminPath, [MEMBERS_INVITE_EXISTS]));
      await expectMembersReady(page);

      await page.locator('app-members-page [data-members-invite-open]').click();
      const dialog = page.locator('[data-members-dialog="invite"][data-state="open"]');
      await expect(dialog).toBeVisible();
      // The address that is already `pending` in the corpus: exactly the collision `invite_exists`
      // describes, so the case reads as the real scenario it is.
      await dialog.locator('input[type="email"]').fill(pendingInviteEmail());
      await dialog.locator('[data-members-submit]').click();

      await expectServerMessage(
        dialog.locator('[data-members-error]'),
        INVITE_EXISTS_CODE,
        INVITE_EXISTS_MESSAGE,
      );

      // Still open, still holding the attempt: a failed invite is not a silent no-op.
      await expect(dialog).toBeVisible();
    });
  }
});

test.describe('a caller without membership:read gets the not-found page, not a refusal', () => {
  test('the members address is indistinguishable from a path that never existed', async ({
    page,
  }) => {
    const refused = withVariants(membersPath('en'), [SETTINGS_NO_TAB_GRANTS]);
    const unknown = withVariants(settingsTabPath('en', 'zzz-unknown'), [SETTINGS_NO_TAB_GRANTS]);
    const rendered = await expectIndistinguishableNotFound(page, [refused, unknown]);

    // The refusal never names what it refused: the segment is absent from the copy.
    expect(rendered.map((entry) => entry.text).join(' ')).not.toContain('members');
    // And no part of the tab rendered behind the pattern.
    await expect(page.locator('app-members-page')).toHaveCount(0);
  });
});

test.describe('a pending invitation is cancelled, not removed', () => {
  for (const locale of LOCALES) {
    test(`removing a pending invite shows the 409 invalid_transition (${locale})`, async ({
      page,
    }) => {
      const adminPath = withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]);
      await page.goto(withVariants(adminPath, [MEMBERS_REMOVE_INVALID_TRANSITION]));
      await expectMembersReady(page);

      const pending = MEMBERSHIPS.find((membership) => membership.status === 'pending');
      if (pending === undefined) {
        throw new Error('fixtures/web-api/memberships-list holds no pending invitation');
      }
      const row = page.locator(`${MEMBER_ROWS}[data-membership-id="${pending.id}"]`);
      await row.locator('[data-members-remove]').click();
      const dialog = page.locator('[data-members-dialog="remove"][data-state="open"]');
      await expect(dialog).toBeVisible();
      await dialog.locator('[data-members-submit]').click();

      // The transition the server refuses, in its own words again: only the client's guess about
      // which rows are removable would have pre-empted it, and the UI makes no such guess.
      await expectServerMessage(
        dialog.locator('[data-members-error]'),
        REMOVE_INVALID_TRANSITION_CODE,
        REMOVE_INVALID_TRANSITION_MESSAGE,
      );
      await expect(dialog).toBeVisible();
      await expect(row).toHaveCount(1);
    });
  }
});

test.describe('the Members tab is WCAG 2.2 AA clean (00 §5.6 W9)', () => {
  for (const theme of THEMES) {
    test(`ready page, en bundle, ${theme} tokens`, async ({ page }) => {
      await seedStoredTheme(page, theme);
      await page.goto(withVariants(membersPath('en'), [SETTINGS_ADMIN_GRANTS]));
      await expectMembersReady(page);
      await expect(page.locator(MEMBER_ROWS)).toHaveCount(MEMBERSHIPS.length);
      await expect(page.locator('app-members-page [data-members-matrix]')).toHaveCount(1);

      await expectNoA11yViolations(page, `members en/${theme}`);

      // The invite dialog is overlaid content, so it is scanned once (light) on top of the page it
      // covers: a modal that is clean alone and broken over the table would otherwise pass.
      if (theme === 'light') {
        await page.locator('app-members-page [data-members-invite-open]').click();
        await expect(
          page.locator('[data-members-dialog="invite"][data-state="open"]'),
        ).toBeVisible();
        await waitForDialogAnimations(page, '[data-members-dialog="invite"][data-state="open"]');
        await expectNoA11yViolations(page, 'members invite dialog en/light');
      }
    });
  }
});

test.describe('an org with no membership yet', () => {
  for (const locale of LOCALES) {
    test(`the empty list renders the shared empty pattern (${locale})`, async ({ page }) => {
      const adminPath = withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]);
      await page.goto(withVariants(adminPath, [MEMBERS_EMPTY_LIST]));
      await expectMembersReady(page);

      // Ready with nothing in it — not the loading skeleton and not an error: the shared state
      // component answers `empty` on its own (09.1.1's `app-request-state`).
      await expect(page.locator('app-empty-pattern')).toHaveCount(1);
      await expect(page.locator('app-error-pattern')).toHaveCount(0);
      await expect(page.locator('app-loading-pattern')).toHaveCount(0);
      await expect(page.locator(MEMBER_ROWS)).toHaveCount(0);
      await expect(page.locator('app-members-page [data-members-matrix]')).toHaveCount(0);
    });
  }
});

test.describe('members matrix responsive records', () => {
  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`intact desktop words and labelled narrow records (${locale}/${theme})`, async ({
        page,
      }) => {
        await page.setViewportSize({ width: 1440, height: 900 });
        await seedStoredTheme(page, theme);
        await page.goto(withVariants(membersPath(locale), [SETTINGS_ADMIN_GRANTS]));
        await expectMembersReady(page);
        const matrix = page.locator('[data-members-matrix]');
        const statuses = matrix.locator('tbody td > span:last-child');
        expect(await statuses.count()).toBeGreaterThan(0);
        for (const header of await matrix.locator('thead th').all()) {
          await expect(header).toHaveCSS('white-space', 'nowrap');
        }
        for (const status of await statuses.all()) {
          await expect(status).toHaveCSS('overflow-wrap', 'normal');
          await expect(status).toHaveCSS('word-break', 'normal');
          expect(
            await status.evaluate((node) => {
              const text = node.firstChild;
              if (text?.nodeType !== Node.TEXT_NODE) return false;
              for (const word of (text.textContent ?? '').matchAll(/\S+/gu)) {
                const range = document.createRange();
                range.setStart(text, word.index);
                range.setEnd(text, word.index + word[0].length);
                if (range.getClientRects().length !== 1) return false;
              }
              return true;
            }),
          ).toBe(true);
        }
        for (const width of [390, 360]) {
          await page.setViewportSize({ width, height: 844 });
          const cells = matrix.locator('tbody td');
          for (const cell of await cells.all()) {
            await expect(cell).toHaveCSS('display', 'grid');
            await expect(cell.locator('.record-label')).toBeVisible();
            await expect(cell).toHaveAttribute('role', 'cell');
            await expect(cell.locator('.record-label')).not.toHaveAttribute('aria-hidden');
          }
          const identity = matrix.locator('tbody th').first();
          await expect(identity.locator('span:last-child')).toHaveText(
            MEMBERSHIPS[0]?.invited_email ?? '',
          );
          expect(
            await page.evaluate(
              () => document.documentElement.scrollWidth <= document.documentElement.clientWidth,
            ),
          ).toBe(true);
          await expect(
            page.locator('app-members-page [data-members-invite-open]'),
          ).not.toBeVisible();
        }
      });
    }
  }
});
