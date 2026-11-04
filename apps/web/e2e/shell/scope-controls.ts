import { expect, type Locator, type Page } from '@playwright/test';

export function scopeControl(page: Page, index: number): Locator {
  return page.locator('app-scope-switcher [role="combobox"]').nth(index);
}

export function scopeOption(page: Page, value: string): Locator {
  return page.locator(`[role="option"][data-value="${value}"]`);
}

export async function expectScopeValue(control: Locator, value: string): Promise<void> {
  await expect(control.locator('xpath=ancestor::hlm-select')).toHaveAttribute('data-value', value);
}

export async function chooseScope(page: Page, control: Locator, value: string): Promise<void> {
  await control.click();
  await scopeOption(page, value).click();
}

// Brain keeps focus on the combobox and exposes keyboard exploration via active-descendant.
export async function exploreScope(page: Page, control: Locator, value: string): Promise<void> {
  await expect(control).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(control).toHaveAttribute('aria-expanded', 'true');
  const options = page.getByRole('option');
  await expect(options.first()).toBeVisible();
  const count = await options.count();
  await page.keyboard.press('Home');
  for (let index = 0; index < count; index += 1) {
    if ((await options.nth(index).getAttribute('data-value')) === value) {
      const id = await options.nth(index).getAttribute('id');
      if (id === null) throw new Error('Scope option has no active-descendant id');
      await expect(control).toHaveAttribute('aria-activedescendant', id);
      return;
    }
    await page.keyboard.press('ArrowDown');
  }
  throw new Error(`Missing scope option ${value}`);
}
