import { expect, type Locator, type Page } from '@playwright/test';

/**
 * Locale-independent locators for the run list and the detail page opened by its subject link.
 * The tab strip always renders Evidence before Decision.
 */
export class RunDetailPage {
  constructor(private readonly page: Page) {}

  get list(): Locator {
    return this.page.locator('app-run-list-page');
  }

  get rows(): Locator {
    return this.page.locator('app-run-list-table [data-run-row]');
  }

  row(runId: string): Locator {
    return this.page.locator(`[data-run-row][data-run-id="${runId}"]`);
  }

  /** The run's subject link in its list row. */
  runLink(runId: string): Locator {
    return this.row(runId).getByRole('link');
  }

  get detail(): Locator {
    return this.page.locator('app-run-detail-page');
  }

  get headerId(): Locator {
    return this.detail.locator('app-run-header [data-run-id] .identifier-accessible bdi');
  }

  get headerRef(): Locator {
    return this.detail.locator('app-run-header [data-run-ref] > bdi');
  }

  get contextVersion(): Locator {
    return this.detail.locator('app-run-context [data-run-version]');
  }

  get evidencePanel(): Locator {
    return this.detail.locator('#evidence-panel');
  }

  get missingReasonCode(): Locator {
    return this.evidencePanel.locator('[data-evidence-missing] .reason-code code');
  }

  get decisionPanel(): Locator {
    return this.detail.locator('#decision-panel');
  }

  get decisionChip(): Locator {
    return this.decisionPanel.locator('app-decision-chip [data-tone]');
  }

  /** Opens a list address and waits for the table's own ready state, not for a row count. */
  async openList(address: string): Promise<void> {
    await this.page.goto(address);
    await expect(this.list).toHaveAttribute('data-run-list-status', 'ready');
  }

  /** Clicks one row and waits for the detail page it navigates to; the list must be open. */
  async openRun(runId: string): Promise<void> {
    await this.runLink(runId).click();
    await this.awaitDetail();
  }

  /** Opens a detail address directly, the way a shared review link would. */
  async openDetail(address: string): Promise<void> {
    await this.page.goto(address);
    await this.awaitDetail();
  }

  async awaitDetail(): Promise<void> {
    await expect(this.detail).toHaveAttribute('data-run-detail-status', 'ready');
  }

  async showContext(): Promise<void> {
    await this.detail.locator('[data-run-tab="context"]').click();
    await expect(this.contextVersion).toBeVisible();
  }

  /** Select by stable tab identity, independent of locale. */
  async showDecision(): Promise<void> {
    await this.detail.locator('[data-run-tab="decision"]').click();
    await expect(this.decisionPanel).toBeVisible();
  }

  /** The page's own text, collapsed, for "this verdict appears nowhere" assertions. */
  async text(): Promise<string> {
    return ((await this.detail.textContent()) ?? '').replace(/\s+/g, ' ').trim();
  }
}
