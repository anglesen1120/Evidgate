import { ComponentHarness } from '@angular/cdk/testing';

export class CatalogPageHarness extends ComponentHarness {
  static hostSelector = 'app-case-list-page';
  readonly headings = this.locatorForAll('[role="columnheader"]');
  readonly table = this.locatorForOptional('app-data-table');
  readonly loading = this.locatorForOptional('app-loading-pattern');
  readonly empty = this.locatorForOptional('app-empty-pattern');
  readonly error = this.locatorForOptional('app-error-pattern');
  readonly forbidden = this.locatorForOptional('app-forbidden-pattern');
  readonly more = this.locatorForOptional('[data-catalog-load-more]');
  readonly loaded = this.locatorFor('[data-catalog-loaded]');
  readonly rows = this.locatorForAll('app-case-list-table [data-row-id]');
  readonly viewport = this.locatorForOptional('cdk-virtual-scroll-viewport');
  readonly ownerFailure = this.locatorForOptional('[data-catalog-owner-error]');
  readonly ownerPending = this.locatorForOptional('[data-catalog-owner-pending]');
  readonly pageError = this.locatorForOptional('[data-catalog-page-error]');
  readonly summary = this.locatorFor('[data-catalog-summary]');
  readonly status = this.locatorFor('#catalog-status');
  readonly search = this.locatorFor('#catalog-search');
  readonly reloadOwner = this.locatorFor('[data-catalog-owner] button[data-catalog-owner-reload]');
  async text() {
    return (await this.host()).text();
  }
  async columnLabels() {
    return Promise.all((await this.headings()).map((heading) => heading.text()));
  }
  async chooseOwner(caseId: string, email: string) {
    const trigger = await this.ownerTrigger(caseId);
    await trigger.click();
    const options = await this.documentRootLocatorFactory().locatorForAll('[role="option"]')();
    for (const option of options) {
      if ((await option.text()).includes(email)) {
        await option.click();
        return;
      }
    }
    throw new Error(`Owner option ${email} is missing`);
  }
  ownerTrigger(caseId: string) {
    return this.locatorFor(`[data-catalog-owner="${caseId}"] hlm-select-trigger button`)();
  }
  async choosePageSize(size: number) {
    await (await this.locatorFor('#catalog-page-size')()).click();
    const options = await this.documentRootLocatorFactory().locatorForAll('[role="option"]')();
    for (const option of options) {
      if ((await option.text()).trim() === String(size)) {
        await option.click();
        return;
      }
    }
    throw new Error(`Page size ${size} is missing`);
  }
}
