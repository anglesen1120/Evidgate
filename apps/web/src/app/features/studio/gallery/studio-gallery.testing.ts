import { ComponentHarness } from '@angular/cdk/testing';

export class GalleryHarness extends ComponentHarness {
  static hostSelector = 'app-studio-gallery-review';
  readonly cards = this.locatorForAll('app-gallery-template-card');
  readonly search = this.locatorFor('input[type="search"]');
  readonly pick = this.locatorForAll('app-gallery-template-card button');
  readonly preview = this.locatorFor('#gallery-preview');
  readonly create = this.locatorFor('[data-gallery-create]');
  readonly recover = this.locatorFor('app-empty-pattern button, app-inline-feedback button');
}

export class DraftHarness extends ComponentHarness {
  static hostSelector = '[role="dialog"]';
  readonly assertion = this.locatorFor('#gallery-assertion');
  readonly submit = this.locatorFor('[data-gallery-submit]');
  readonly cancel = this.locatorFor('[data-gallery-cancel]');
}
