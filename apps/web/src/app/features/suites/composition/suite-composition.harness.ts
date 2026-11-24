import { ComponentHarness } from '@angular/cdk/testing';

export class SuiteCompositionHarness extends ComponentHarness {
  static hostSelector = 'app-suite-composition';
  readonly flatRows = this.locatorForAll('[data-suite-flat-member]');
  readonly memberIds = this.locatorForAll('[data-suite-flat-member-id]');
  readonly summary = this.locatorFor('app-data-table-pager [role="status"]');
  readonly previous = this.locatorFor('button[data-page="previous"]');
  readonly next = this.locatorFor('button[data-page="next"]');
  readonly size = this.locatorFor('app-data-table-pager hlm-select-trigger button');
  readonly options = this.documentRootLocatorFactory().locatorForAll('[role="option"]');

  async ids(): Promise<string[]> {
    return Promise.all((await this.memberIds()).map((row) => row.text()));
  }

  async chooseSize(size: number): Promise<void> {
    await (await this.size()).click();
    for (const option of await this.options()) {
      if ((await option.text()) === String(size)) {
        await option.click();
        return;
      }
    }
    throw new Error(`Missing page size ${size}`);
  }
}
