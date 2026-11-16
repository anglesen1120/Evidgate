import { expect, type Locator, type Page } from '@playwright/test';

export const MEMBER_ROWS = 'app-members-page app-member-list [data-row-id]';
export type MemberAction = 'edit-open' | 'resend-open' | 'cancel-invite-open' | 'remove';

export function memberRow(page: Page, membershipId: string): Locator {
  return page.locator(`${MEMBER_ROWS}[data-row-id="${membershipId}"]`);
}

// The menu item is portalled and transient; dialogs restore the persistent row trigger.
export async function openMemberMenu(page: Page, row: Locator, keyboard = false): Promise<Locator> {
  const trigger = row.locator('[data-members-actions]');
  if (keyboard) {
    await trigger.focus();
    await page.keyboard.press('Enter');
  } else {
    await trigger.click();
  }
  await expect(page.getByRole('menu')).toBeVisible();
  return trigger;
}

export async function openMemberAction(
  page: Page,
  row: Locator,
  action: MemberAction,
  keyboard = false,
): Promise<Locator> {
  const trigger = await openMemberMenu(page, row, keyboard);
  const item = page.locator(`[data-members-${action}]`);
  await expect(item).toHaveCount(1);
  if (keyboard) {
    await page.keyboard.press('Home');
    const count = await page.getByRole('menuitem').count();
    for (let step = 0; step < count; step += 1) {
      if (await item.evaluate((element) => element === document.activeElement)) break;
      await page.keyboard.press('ArrowDown');
    }
    await expect(item).toBeFocused();
    await page.keyboard.press('Enter');
  } else {
    await item.click();
  }
  return trigger;
}

export async function expectIntactWords(content: Locator): Promise<void> {
  const intact = await content.evaluate((element) => {
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    let words = 0;
    while (walker.nextNode()) {
      const text = walker.currentNode;
      for (const word of (text.textContent ?? '').matchAll(/\S+/gu)) {
        const range = document.createRange();
        range.setStart(text, word.index);
        range.setEnd(text, word.index + word[0].length);
        if (range.getClientRects().length !== 1) return false;
        words += 1;
      }
    }
    return words > 0;
  });
  expect(intact, 'a header or status word split across lines').toBe(true);
}
