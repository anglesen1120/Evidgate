import { ComponentHarness } from '@angular/cdk/testing';

export class CopyValueButtonHarness extends ComponentHarness {
  static hostSelector = 'app-copy-value-button';
  private readonly button = this.locatorFor('button');
  private readonly status = this.locatorFor('[role="status"]');

  async copy(): Promise<void> {
    await (await this.button()).click();
  }

  async actionName(): Promise<string | null> {
    return (await this.button()).getAttribute('aria-label');
  }

  async result(): Promise<string> {
    return (await this.status()).text();
  }

  async visibleText(): Promise<string> {
    return (await this.button()).text();
  }
}

export class CopyableIdentifierHarness extends ComponentHarness {
  static hostSelector = 'app-copyable-identifier';
  private readonly preview = this.locatorFor('code');
  private readonly disclosure = this.locatorFor('button[aria-haspopup="dialog"]');
  private readonly accessible = this.locatorFor('.identifier-accessible');
  private readonly fullValue = this.documentRootLocatorFactory().locatorFor('[role="dialog"] code');
  private readonly close = this.documentRootLocatorFactory().locatorFor(
    '[role="dialog"] button[hlmDialogClose]',
  );

  async previewText(): Promise<string> {
    return (await this.preview()).text();
  }

  async accessibleValue(): Promise<string> {
    return (await this.accessible()).text();
  }

  async showFull(): Promise<void> {
    await (await this.disclosure()).click();
  }

  async fullText(): Promise<string> {
    return (await this.fullValue()).text();
  }

  async closeFull(): Promise<void> {
    await (await this.close()).click();
  }
}

export class NumberValueHarness extends ComponentHarness {
  static hostSelector = 'app-number-value';
  private readonly groups = this.locatorForAll('.number-group');

  async digitGroups(): Promise<string[]> {
    return Promise.all((await this.groups()).map((group) => group.text()));
  }

  async text(): Promise<string> {
    return (await this.host()).text();
  }
}
