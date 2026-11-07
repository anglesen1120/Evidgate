import { type Locator, type Page } from '@playwright/test';

/** The org/project scope switcher of the app shell, as the core specs drive it. */
export class ScopeSwitcher {
  constructor(private readonly page: Page) {}

  control(index: number): Locator {
    return this.page.locator('app-scope-switcher [role="combobox"]').nth(index);
  }

  option(value: string): Locator {
    return this.page.locator(`[role="option"][data-value="${value}"]`);
  }

  async choose(index: number, value: string): Promise<void> {
    await this.control(index).click();
    await this.option(value).click();
  }
}
