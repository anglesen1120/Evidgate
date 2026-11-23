import { ComponentHarness } from '@angular/cdk/testing';

export class SettingsHarness extends ComponentHarness {
  static hostSelector = 'app-ai-settings-page';
  readonly saveButton = this.locatorFor('[data-ai-save]');
  readonly retryButton = this.locatorFor('app-retry-after button');
  readonly dialog = this.documentRootLocatorFactory().locatorForOptional('[role="dialog"]');
  readonly dialogTitle = this.documentRootLocatorFactory().locatorFor('[role="dialog"] h2');
  readonly dialogContent = this.documentRootLocatorFactory().locatorFor('hlm-dialog-content');
  readonly dialogSubject = this.documentRootLocatorFactory().locatorFor('[hlmDialogSubject]');
  readonly dialogStatusIcon = this.documentRootLocatorFactory().locatorFor('[hlmDialogStatusIcon]');
  readonly view = this.locatorFor('[data-ai-version-view]');
  readonly settingsTab = this.locatorFor('[role="tab"]:first-child');
  readonly historyTab = this.locatorFor('[data-ai-history-tab]');
  readonly emptyTitle = this.locatorFor('app-ai-settings-history app-empty-pattern h2');
  readonly nextHistoryPage = this.locatorFor(
    'nav[aria-label="Version history pages"] button:last-child',
  );
  async attribute(selector: string, name: string) {
    return (await this.locatorFor(selector)()).getAttribute(name);
  }
  async count(selector: string) {
    return (await this.locatorForAll(selector)()).length;
  }
  async accessibleLabel(selector: string) {
    const id = await this.attribute(selector, 'aria-labelledby');
    return (await this.locatorFor('#' + id)()).text();
  }
  async fill(key: string, value: string) {
    const field = await this.locatorFor('#ai-' + key)();
    await field.setInputValue(value);
    await field.dispatchEvent('input');
    await field.dispatchEvent('blur');
  }
  async value(key: string) {
    return (await this.locatorFor('#ai-' + key)()).getProperty<string>('value');
  }
  async click(selector: string) {
    await (await this.locatorFor(selector)()).click();
  }
  async compose(key: string, active: boolean) {
    await (
      await this.locatorFor('#ai-' + key)()
    ).dispatchEvent(active ? 'compositionstart' : 'compositionend');
  }
  async text() {
    return (await this.host()).text();
  }
}
