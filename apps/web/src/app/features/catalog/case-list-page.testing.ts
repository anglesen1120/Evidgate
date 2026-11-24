import { ComponentHarness } from '@angular/cdk/testing';

export class CatalogPageHarness extends ComponentHarness {
  static hostSelector = 'app-case-list-page';
  readonly headings = this.locatorForAll('[role="columnheader"]');
  readonly table = this.locatorForOptional('app-data-table');
  readonly loading = this.locatorForOptional('app-loading-pattern');
  readonly empty = this.locatorForOptional('app-empty-pattern');
  readonly error = this.locatorForOptional('app-error-pattern');
  readonly forbidden = this.locatorForOptional('app-forbidden-pattern');
  readonly next = this.locatorFor('app-data-table-pager [data-page="next"]');
  readonly previous = this.locatorFor('app-data-table-pager [data-page="previous"]');
  readonly pageNumber = this.locatorFor('app-data-table-pager [role="status"]');
  readonly retryPage = this.locatorFor('[data-catalog-page-error] button');
  readonly loaded = this.locatorFor('app-page-header .header-description');
  readonly rows = this.locatorForAll('app-case-list-table [data-row-id]');
  readonly ownerFailure = this.locatorForOptional('[data-catalog-owner-error]');
  readonly ownerPending = this.locatorForOptional('[data-catalog-owner-pending]');
  readonly pageError = this.locatorForOptional('[data-catalog-page-error]');
  readonly summary = this.locatorFor('[data-catalog-summary]');
  readonly status = this.locatorFor('#catalog-status');
  readonly search = this.locatorFor('#catalog-search');
  readonly reloadOwner = this.documentRootLocatorFactory().locatorFor(
    '#catalog-record-error button',
  );
  readonly dialog = this.documentRootLocatorFactory().locatorForOptional('[role="dialog"]');
  readonly dialogTitle = this.documentRootLocatorFactory().locatorFor('#catalog-record-title');
  readonly keep = this.documentRootLocatorFactory().locatorFor('[data-catalog-keep]');
  readonly discard = this.documentRootLocatorFactory().locatorFor('[data-catalog-discard]');
  readonly cancel = this.documentRootLocatorFactory().locatorFor('[data-catalog-cancel]');
  readonly save = this.documentRootLocatorFactory().locatorFor('[data-catalog-save]');
  view(caseId: string) {
    return this.locatorFor(`[data-row-id="${caseId}"] [data-catalog-view]`)();
  }
  edit(caseId: string) {
    return this.locatorForOptional(`[data-row-id="${caseId}"] [data-catalog-edit]`)();
  }
  ownerValue(caseId: string) {
    return this.locatorFor(`[data-catalog-owner="${caseId}"]`)();
  }
  async text() {
    return (await this.host()).text();
  }
  async columnLabels() {
    return Promise.all((await this.headings()).map((heading) => heading.text()));
  }
  async chooseOwner(caseId: string, email: string) {
    await this.draftOwner(caseId, email);
    await (await this.save()).click();
  }
  async draftOwner(caseId: string, email: string) {
    const edit = await this.edit(caseId);
    if (!edit) throw new Error('Edit owner not available');
    await edit.click();
    await this.forceStabilize();
    await (await this.documentRootLocatorFactory().locatorFor('#catalog-record-owner')()).click();
    await this.forceStabilize();
    const options = await this.documentRootLocatorFactory().locatorForAll('[role="option"]')();
    for (const option of options) {
      if ((await option.text()).includes(email)) {
        await option.click();
        await this.forceStabilize();
        return;
      }
    }
    throw new Error(`Owner option ${email} is missing`);
  }
  async choosePageSize(size: number) {
    await (await this.locatorFor('app-data-table-pager hlm-select-trigger button')()).click();
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
