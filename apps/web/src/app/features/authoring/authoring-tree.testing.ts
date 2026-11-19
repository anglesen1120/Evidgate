import { ComponentHarness } from '@angular/cdk/testing';

export class AuthoringTreeHarness extends ComponentHarness {
  static hostSelector = 'app-authoring-tree-pane';
  readonly treeRoles = this.locatorForAll('[role="tree"], [role="treeitem"]');
  readonly nestedLists = this.locatorForAll('li > ul:not([hidden])');
  readonly loading = this.locatorForOptional('app-loading-pattern');
  readonly errorRetry = this.locatorFor('app-error-pattern button');
  readonly forbidden = this.locatorForOptional('app-forbidden-pattern');
  readonly capped = this.locatorForAll('[data-tree-browse-capped] [role="status"]');
  readonly moreControls = this.locatorForAll('[data-more]');
  nodeRows = this.locatorForAll('[data-node-id]');
  /** Matches the attribute value itself, so a node id carrying CSS syntax still finds its row. */
  private async control(kind: 'data-disclosure' | 'data-scope' | 'data-more', id: string) {
    for (const element of await this.locatorForAll(`[${kind}]`)()) {
      if ((await element.getAttribute(kind)) === id) return element;
    }
    throw new Error(`No [${kind}] control carries the value ${id}`);
  }
  disclosure(id: string) {
    return this.control('data-disclosure', id);
  }
  scope(id: string) {
    return this.control('data-scope', id);
  }
  more(id: string) {
    return this.control('data-more', id);
  }
  async text() {
    return (await this.host()).text();
  }
  async visibleIds() {
    return Promise.all((await this.nodeRows()).map((row) => row.getAttribute('data-node-id')));
  }
}

export class AuthoringPageHarness extends ComponentHarness {
  static hostSelector = 'app-authoring-page';
  readonly heading = this.locatorFor('#authoring-page-title');
  readonly tree = this.locatorForOptional(AuthoringTreeHarness);
  readonly table = this.locatorFor('[data-authoring-cases]');
  readonly loading = this.locatorForOptional('app-loading-pattern');
  readonly empty = this.locatorForOptional('app-empty-pattern:not(app-authoring-case-table *)');
  readonly error = this.locatorForOptional('app-error-pattern');
  readonly forbidden = this.locatorForOptional('app-forbidden-pattern');
  readonly notFound = this.locatorForOptional('app-not-found-pattern');
  readonly stale = this.locatorForOptional('[data-authoring-stale]');
  readonly breadcrumbs = this.locatorForAll('[data-authoring-breadcrumb] li');
  readonly refresh = this.locatorFor('[data-authoring-refresh]');
  readonly selectedId = this.locatorForOptional('app-copyable-identifier');
  async text() {
    return (await this.host()).text();
  }
  async path() {
    return Promise.all(
      (await this.breadcrumbs()).map((crumb) => crumb.text({ exclude: '[aria-hidden="true"]' })),
    );
  }
}
