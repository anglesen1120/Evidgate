import { ComponentHarness } from '@angular/cdk/testing';

export class ProducerStatusTableHarness extends ComponentHarness {
  static hostSelector = 'app-producer-status-table';
  readonly rows = this.locatorForAll('tr[data-row-id]');
  readonly summary = this.locatorFor('app-data-table-pager .page-range');
  readonly previous = this.locatorFor('button[data-page="previous"]');
  readonly next = this.locatorFor('button[data-page="next"]');
  readonly size = this.locatorFor('app-data-table-pager hlm-select-trigger button');
  readonly options = this.documentRootLocatorFactory().locatorForAll('[role="option"]');

  async rowIds(): Promise<(string | null)[]> {
    return Promise.all((await this.rows()).map((row) => row.getAttribute('data-row-id')));
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
