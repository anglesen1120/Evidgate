import { ComponentHarness } from '@angular/cdk/testing';

export class GalleryHarness extends ComponentHarness {
  static hostSelector = 'app-studio-gallery-review';
  readonly rows = this.locatorForAll('[data-row-id]');
  readonly table = this.locatorFor('[role="table"]');
  readonly headers = this.locatorForAll('[role="columnheader"]');
  readonly search = this.locatorFor('input[type="search"]');
  readonly source = (key: string) => this.locatorFor(`[data-gallery-source="${key}"]`)();
  readonly pick = this.locatorForAll('[data-gallery-view]');
  readonly create = this.locatorFor('[data-gallery-create]');
  readonly recover = this.locatorFor('app-empty-pattern button, app-inline-feedback button');
}

export class ViewHarness extends ComponentHarness {
  static hostSelector = '[role="dialog"]:has([data-gallery-view-title])';
  readonly title = this.locatorFor('[data-gallery-view-title]');
  readonly close = this.locatorFor('footer button');
  readonly terms = this.locatorForAll('dl dt');
  readonly isolatedFacts = this.locatorForAll('dl dd bdi');
  readonly create = this.locatorForOptional('[data-gallery-view-create]');
}

export class BackdropHarness extends ComponentHarness {
  static hostSelector = '.cdk-overlay-backdrop';
}

export class TemplateOptionHarness extends ComponentHarness {
  static hostSelector = 'hlm-select-item';
}

export class DraftHarness extends ComponentHarness {
  static hostSelector = '[role="dialog"]';
  readonly template = this.locatorFor('#gallery-form-template');
  readonly title = this.locatorFor('[data-gallery-draft-title]');
  readonly assertion = this.locatorFor('#gallery-assertion');
  readonly submit = this.locatorFor('[data-gallery-submit]');
  readonly cancel = this.locatorFor('[data-gallery-cancel]');
  readonly close = this.locatorFor('header button');
  readonly keep = this.locatorFor('[data-gallery-keep]');
  readonly discard = this.locatorFor('[data-gallery-discard]');
  readonly variant = this.locatorFor('dl dd bdi');
}
