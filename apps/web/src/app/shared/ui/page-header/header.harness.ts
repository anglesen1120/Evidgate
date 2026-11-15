import { ComponentHarness } from '@angular/cdk/testing';

export class HeaderHarness extends ComponentHarness {
  static hostSelector = 'app-page-header, app-section-header';

  private readonly heading = this.locatorFor('[data-slot="card-title"]');
  private readonly description = this.locatorForOptional('[data-slot="card-description"]');
  private readonly action = this.locatorForOptional('button');
  private readonly headings = this.locatorForAll('h1, h2, h3');
  private readonly cards = this.locatorForAll('[data-slot="card"]');

  async getHeadingCount(): Promise<number> {
    return (await this.headings()).length;
  }

  async getCardCount(): Promise<number> {
    return (await this.cards()).length;
  }

  async getTitle(): Promise<string> {
    return (await this.heading()).text();
  }

  async getHeadingLevel(): Promise<string> {
    return (await this.heading()).getProperty<string>('tagName');
  }

  async getTitleId(): Promise<string | null> {
    return (await this.heading()).getAttribute('id');
  }

  async getSize(): Promise<string | null> {
    return (await this.heading()).getAttribute('data-title-size');
  }

  async getDescription(): Promise<string | null> {
    return (await this.description())?.text() ?? null;
  }

  async hasAction(): Promise<boolean> {
    return (await this.action()) !== null;
  }

  async clickAction(): Promise<void> {
    await (await this.action())?.click();
  }

  async isActionDisabled(): Promise<boolean> {
    return (await this.action())?.getProperty<boolean>('disabled') ?? false;
  }

  async isActionWrappingEnabled(): Promise<boolean> {
    const action = await this.action();
    return (
      action !== null &&
      (await action.hasClass('eg-header-action')) &&
      (await action.hasClass('box-border')) &&
      (await action.hasClass('h-auto!')) &&
      (await action.hasClass('whitespace-normal!'))
    );
  }

  async focusAction(): Promise<void> {
    await (await this.action())?.focus();
  }

  async isActionFocused(): Promise<boolean> {
    return (await this.action())?.isFocused() ?? false;
  }

  async getContent(): Promise<string> {
    return (await this.host()).text();
  }
}
