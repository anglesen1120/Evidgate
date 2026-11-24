import { ComponentHarness } from '@angular/cdk/testing';

export class QuotaPanelHarness extends ComponentHarness {
  static hostSelector = 'app-quota-panel';
  readonly metrics = this.locatorForAll('[data-quota-metric]');
  readonly pageTitle = this.locatorForOptional('h1#quota-title');
  readonly sectionTitle = this.locatorForOptional('h2#quota-title');
  readonly subsectionTitles = this.locatorForAll('h3');
  readonly metricHeading = this.locatorFor('[data-quota-metric] h3');
  readonly projectHeading = this.locatorFor('[data-quota-projects] h3');
  readonly projectRows = this.locatorForAll('app-data-table [data-row-id]');
  readonly viewActions = this.locatorForAll('button[data-quota-view]');
  readonly viewIcons = this.locatorForAll('button[data-quota-view] hlm-icon');
  readonly recordedRows = this.locatorForAll('table[aria-labelledby="quota-state-title"] tbody tr');
  readonly pagers = this.locatorForAll('app-data-table-pager');
  readonly pagerNext = this.locatorFor('app-data-table-pager [data-page="next"]');
  readonly pagerPrevious = this.locatorFor('app-data-table-pager [data-page="previous"]');
  readonly pageError = this.locatorForOptional('[data-quota-page-error]');
  readonly retryPage = this.locatorFor('[data-quota-page-error] button');
  readonly sizeTrigger = this.locatorFor('app-data-table-pager hlm-select-trigger button');
  readonly sizeOptions = this.documentRootLocatorFactory().locatorForAll('[role="option"]');
  readonly retry = this.locatorFor('button');
}
