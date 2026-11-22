import { ComponentHarness } from '@angular/cdk/testing';

export class QualityReviewHarness extends ComponentHarness {
  static hostSelector =
    'app-quality-overview-review, app-quality-history, app-quality-breakdown, app-quality-outcome-mix';

  async content(): Promise<string> {
    return (await this.host()).text();
  }

  async exists(selector: string): Promise<boolean> {
    return (await this.locatorForOptional(selector)()) !== null;
  }

  async text(selector: string): Promise<string> {
    return (await this.locatorFor(selector)()).text();
  }

  async click(selector: string): Promise<void> {
    await (await this.locatorFor(selector)()).click();
  }

  async attribute(selector: string, attribute: string): Promise<string | null> {
    return (await this.locatorFor(selector)()).getAttribute(attribute);
  }
}
