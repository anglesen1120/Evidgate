import { registerLocaleData } from '@angular/common';
import localeVi from '@angular/common/locales/vi';
import { LOCALE_ID } from '@angular/core';
import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import type { PolicyRevisionSummaryDto } from '@evidgate/sdk';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { RevisionListComponent } from './revision-list.component';

registerLocaleData(localeVi);
provideLayoutTestStubs();
const row: PolicyRevisionSummaryDto = {
  id: '3e496610-072b-4d48-a789-6d423838c603',
  revision_no: 3,
  hash: 'a123456789abcdef0123456789abcdef',
  state: 'active',
  created_by: 'actor',
  created_at: '2026-10-26T06:00:00Z',
  version: 1,
};
class RevisionHistoryHarness extends ComponentHarness {
  static hostSelector = 'app-policy-revision-list';
  readonly next = this.locatorFor('app-data-table-pager [data-page="next"]');
  readonly previous = this.locatorFor('app-data-table-pager [data-page="previous"]');
  readonly pager = this.locatorFor('app-data-table-pager');
  readonly section = this.locatorFor('section');
  readonly row = this.locatorFor('[data-policy-revision]');
  readonly rows = this.locatorForAll('[data-policy-revision]');
  readonly time = this.locatorFor('time');
  readonly hash = this.locatorFor('.identifier-accessible');
  readonly copyButton = this.locatorFor('app-copy-value-button');
  readonly view = this.locatorFor('[data-policy-view]');
  readonly empty = this.locatorFor('tbody p');
  readonly size = this.locatorFor('app-data-table-pager hlm-select-trigger button');
  readonly options = this.documentRootLocatorFactory().locatorForAll('[role="option"]');
}
describe('RevisionListComponent', () => {
  afterEach(() => TestBed.resetTestingModule());
  it('renders localized state, UTC creation time, full recoverable hash and head marker', async () => {
    TestBed.configureTestingModule({ providers: [{ provide: LOCALE_ID, useValue: 'vi' }] });
    const fixture = TestBed.createComponent(RevisionListComponent);
    fixture.componentRef.setInput('rows', [row]);
    fixture.componentRef.setInput('headId', row.id);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      RevisionHistoryHarness,
    );
    const rendered = await harness.row();
    expect(await rendered.getAttribute('data-revision-no')).toBe('3');
    expect(await rendered.getAttribute('data-revision-state')).toBe(row.state);
    expect(await rendered.getAttribute('data-policy-head')).toBe('');
    expect(await rendered.text()).toContain('Active');
    expect(await (await harness.hash()).text()).toContain(row.hash);
    expect(await (await harness.time()).text()).toBe('06:00:00 26/10/2026 UTC');
    expect(await (await harness.time()).getAttribute('datetime')).toBe(row.created_at);
    expect(await (await harness.section()).getAttribute('aria-labelledby')).toBe(
      'policy-revisions-title',
    );
    expect(await harness.copyButton()).toBeDefined();
    expect(await (await harness.section()).text()).toContain('Oldest first');
  });
  it('keeps empty history labelled and navigation disabled on its only page', async () => {
    const fixture = TestBed.createComponent(RevisionListComponent);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      RevisionHistoryHarness,
    );
    expect(await (await harness.pager()).text()).toContain('Page 1');
    expect(await harness.rows()).toHaveLength(0);
    expect(await (await harness.empty()).text()).toBe('No revisions available.');
    expect(await (await harness.previous()).getAttribute('aria-disabled')).toBe('true');
    expect(await (await harness.next()).getAttribute('aria-disabled')).toBe('true');
  });
  it('emits cursor navigation through the shared pager and blocks busy actions', async () => {
    const fixture = TestBed.createComponent(RevisionListComponent);
    fixture.componentRef.setInput('hasMore', true);
    fixture.componentRef.setInput('page', 2);
    const next = vi.fn();
    const previous = vi.fn();
    fixture.componentInstance.next.subscribe(next);
    fixture.componentInstance.previous.subscribe(previous);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      RevisionHistoryHarness,
    );
    expect(await (await harness.pager()).text()).toContain('Page 2');
    await (await harness.previous()).click();
    expect(previous).toHaveBeenCalledTimes(1);
    await fixture.whenStable();
    await (await harness.next()).click();
    expect(next).toHaveBeenCalledTimes(1);
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    expect(await (await harness.previous()).getAttribute('aria-disabled')).toBe('true');
    expect(await (await harness.next()).getAttribute('aria-disabled')).toBe('true');
    await (await harness.next()).click();
    expect(next).toHaveBeenCalledTimes(1);
  });
  it('makes selection keyboard-operable and exposes the selected detail relationship', async () => {
    const fixture = TestBed.createComponent(RevisionListComponent);
    const selected = vi.fn();
    fixture.componentInstance.selected.subscribe(selected);
    fixture.componentRef.setInput('rows', [row]);
    fixture.componentRef.setInput('selectedId', row.id);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      RevisionHistoryHarness,
    );
    const action = await harness.view();
    await action.focus();
    expect(await action.getAttribute('aria-haspopup')).toBe('dialog');
    await action.click();
    expect(selected).toHaveBeenCalledExactlyOnceWith({
      id: row.id,
      trigger: expect.any(HTMLButtonElement) as unknown,
    });
    fixture.componentRef.setInput('busy', true);
    await action.click();
    expect(selected).toHaveBeenCalledTimes(1);
  });
  it.each([50, 100])('forwards selected history size %i', async (size) => {
    const fixture = TestBed.createComponent(RevisionListComponent);
    const changed = vi.fn();
    fixture.componentInstance.pageSizeChange.subscribe(changed);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      RevisionHistoryHarness,
    );
    expect(await (await harness.size()).text()).toBe('20');
    await (await harness.size()).click();
    const options = await harness.options();
    const texts = await Promise.all(options.map((option) => option.text()));
    expect(texts).toEqual(['20', '50', '100']);
    const option = options[texts.indexOf(String(size))];
    if (!option) throw new Error('Expected supported history size');
    await option.click();
    expect(changed).toHaveBeenCalledExactlyOnceWith(size);
  });
});
