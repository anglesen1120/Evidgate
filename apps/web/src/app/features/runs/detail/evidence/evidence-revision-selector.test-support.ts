import { ComponentHarness } from '@angular/cdk/testing';

export class EvidenceRevisionSelectorHarness extends ComponentHarness {
  static hostSelector = 'app-evidence-revision-selector';
  readonly trigger = this.locatorFor('[data-evidence-revision-select]');
  readonly options = this.documentRootLocatorFactory().locatorForAll('[role="option"]');
  readonly listbox = this.documentRootLocatorFactory().locatorFor('[role="listbox"]');

  async choose(revision: number): Promise<void> {
    await (await this.trigger()).click();
    for (const option of await this.options()) {
      if ((await option.text()).trim().startsWith(`Revision ${revision} ·`)) {
        await option.click();
        return;
      }
    }
    throw new Error(`Missing evidence revision ${revision}`);
  }
}
