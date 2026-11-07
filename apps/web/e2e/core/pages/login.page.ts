import { expect, type Locator, type Page } from '@playwright/test';

/**
 * The sign-in screen an anonymous visitor is sent to. Locators key on the page's own ids and
 * component hooks, so the same object drives the `en` and `vi` bundles: the provider labels are
 * translated and are asserted only by a test whose subject is the label.
 */
export class LoginPage {
  constructor(private readonly page: Page) {}

  get heading(): Locator {
    return this.page.locator('#login-heading');
  }

  /** The provider buttons in render order; the card's other controls are excluded. */
  get providerButtons(): Locator {
    return this.page.locator('.login-providers').getByRole('button');
  }

  get notice(): Locator {
    return this.page.locator('.login-notice');
  }

  /** Both providers are offered and usable: a disabled one would make the screen a dead end. */
  async expectReady(): Promise<void> {
    await expect(this.heading).toBeVisible();
    await expect(this.providerButtons).toHaveCount(2);
    await expect(this.providerButtons.first()).toBeEnabled();
    await expect(this.providerButtons.nth(1)).toBeEnabled();
  }

  /** The path the app resumes after sign-in, or `null` when the refused attempt was the root. */
  returnUrl(): string | null {
    return new URL(this.page.url()).searchParams.get('returnUrl');
  }
}
