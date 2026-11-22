import { ComponentHarness } from '@angular/cdk/testing';

export class EditorHarness extends ComponentHarness {
  static hostSelector =
    'app-studio-draft-editor-review, app-editor-definition, app-editor-validation, app-editor-sandbox, app-editor-versions';
  readonly find = (selector: string) => this.locatorFor(selector)();
  readonly all = (selector: string) => this.locatorForAll(selector)();
  readonly text = async () => (await this.host()).text();
}
