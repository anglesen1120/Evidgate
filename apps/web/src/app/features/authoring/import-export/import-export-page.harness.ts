import { ComponentHarness, TestKey } from '@angular/cdk/testing';

export class ImportPageHarness extends ComponentHarness {
  static hostSelector = 'app-authoring-import-export-page';
  readonly dialog = this.documentRootLocatorFactory().locatorForOptional('[role="dialog"]');
  readonly dialogTitle = this.documentRootLocatorFactory().locatorFor('[role="dialog"] h2');
  text() {
    return this.host().then((host) => host.text());
  }
  async count(selector: string) {
    return (await this.locatorForAll(selector)()).length;
  }
  async has(selector: string) {
    return (await this.locatorForOptional(selector)()) !== null;
  }
  async textAt(selector: string) {
    return (await this.locatorFor(selector)()).text();
  }
  async attr(selector: string, attribute: string) {
    return (await this.locatorFor(selector)()).getAttribute(attribute);
  }
  async click(selector: string) {
    await (await this.locatorFor(selector)()).click();
  }
  async key(selector: string, key: TestKey) {
    await (await this.locatorFor(selector)()).sendKeys(key);
  }
  async focus(selector: string) {
    await (await this.locatorFor(selector)()).focus();
  }
  async focused(selector: string) {
    return (await this.locatorFor(selector)()).isFocused();
  }
}

export class ImportOptionHarness extends ComponentHarness {
  static hostSelector = '[role="option"]';
  text() {
    return this.host().then((host) => host.text());
  }
  async attr(name: string) {
    return (await this.host()).getAttribute(name);
  }
  async click() {
    await (await this.host()).click();
  }
}
