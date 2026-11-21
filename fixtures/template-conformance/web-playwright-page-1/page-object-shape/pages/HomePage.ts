import { type Locator, type Page } from '@playwright/test';

export class HomePage {
  constructor() {}

  async open(): Promise<void> {
    await this.page.goto('https://example.com');
  }

  primaryAction(): Locator {
    return this.page.locator('button');
  }
}
