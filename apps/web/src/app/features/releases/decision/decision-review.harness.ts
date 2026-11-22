import { ComponentHarness } from '@angular/cdk/testing';

export class DecisionReviewHarness extends ComponentHarness {
  static hostSelector =
    'app-release-decision-review, app-decision-exceptions, app-decision-preview';
  readonly find = (selector: string) => this.locatorFor(selector)();
  readonly all = (selector: string) => this.locatorForAll(selector)();
  readonly text = async () => (await this.host()).text();
}

export class DecisionDialogHarness extends ComponentHarness {
  static hostSelector = '[role=dialog]';
  readonly text = async () => (await this.host()).text();
  readonly find = (selector: string) => this.locatorFor(selector)();
  readonly all = (selector: string) => this.locatorForAll(selector)();
}
