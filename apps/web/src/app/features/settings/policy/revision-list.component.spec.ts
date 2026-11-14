import { formatDate, registerLocaleData } from '@angular/common';
import localeVi from '@angular/common/locales/vi';
import { LOCALE_ID } from '@angular/core';
import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import type { PolicyRevisionSummaryDto } from '@evidgate/sdk';
import { rootElement } from './policy-reads.test-support';
import { RevisionListComponent } from './revision-list.component';

registerLocaleData(localeVi);
const row: PolicyRevisionSummaryDto = {
  id: '3e496610-072b-4d48-a789-6d423838c603',
  revision_no: 3,
  hash: 'a123456789abcdef0123456789abcdef',
  state: 'active',
  created_by: 'actor',
  created_at: '2026-10-26T06:00:00Z',
  version: 1,
};
class LoadMoreHarness extends ComponentHarness {
  static hostSelector = 'button[hlmBtn]';
  async activate(): Promise<void> {
    const host = await this.host();
    await host.focus();
    await host.click();
  }
}
describe('RevisionListComponent', () => {
  afterEach(() => TestBed.resetTestingModule());
  it('renders localized state, UTC creation time, full recoverable hash and head marker', () => {
    TestBed.configureTestingModule({ providers: [{ provide: LOCALE_ID, useValue: 'vi' }] });
    const fixture = TestBed.createComponent(RevisionListComponent);
    fixture.componentRef.setInput('rows', [row]);
    fixture.componentRef.setInput('headId', row.id);
    fixture.detectChanges();
    const root = rootElement(fixture);
    const revision = root.querySelector<HTMLElement>('[data-policy-revision]');
    expect(revision?.dataset['revisionNo']).toBe('3');
    expect(revision?.dataset['revisionState']).toBe(row.state);
    expect(revision?.hasAttribute('data-policy-head')).toBe(true);
    expect(revision?.textContent).toContain('Active');
    expect(revision?.querySelector('.identifier-accessible')?.textContent).toContain(row.hash);
    expect(root.querySelector('time')?.textContent?.trim()).toBe(
      `${formatDate(row.created_at, 'medium', 'vi', 'UTC')} UTC`,
    );
    expect(root.querySelector('time')?.getAttribute('datetime')).toBe(row.created_at);
    expect(root.querySelector('section')?.getAttribute('aria-labelledby')).toBe(
      'policy-revisions-title',
    );
    expect(root.querySelector('app-copy-value-button')).not.toBeNull();
  });
  it('shows a labelled empty state and bounded load-more action only when available', () => {
    const fixture = TestBed.createComponent(RevisionListComponent);
    const more = vi.fn();
    fixture.componentInstance.loadMore.subscribe(more);
    fixture.detectChanges();
    const root = rootElement(fixture);
    expect(root.querySelectorAll('[data-policy-revision]')).toHaveLength(0);
    expect(root.querySelector('p')?.textContent).toMatch(/\S/);
    expect(root.querySelector('button')).toBeNull();
    fixture.componentRef.setInput('hasMore', true);
    fixture.detectChanges();
    root.querySelector<HTMLButtonElement>('button')?.click();
    expect(more).toHaveBeenCalledTimes(1);
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    root.querySelector<HTMLButtonElement>('button')?.click();
    expect(more).toHaveBeenCalledTimes(1);
    expect(root.querySelector('section')?.getAttribute('aria-busy')).toBe('true');
  });
  it('keeps the load-more action reachable through the component harness', async () => {
    const fixture = TestBed.createComponent(RevisionListComponent);
    fixture.componentRef.setInput('hasMore', true);
    const more = vi.fn();
    fixture.componentInstance.loadMore.subscribe(more);
    const action = await TestbedHarnessEnvironment.loader(fixture).getHarness(LoadMoreHarness);
    await action.activate();
    expect(more).toHaveBeenCalledTimes(1);
    expect(document.activeElement?.textContent).toContain('Load more');
  });
  it('makes selection keyboard-operable and exposes the selected detail relationship', () => {
    const fixture = TestBed.createComponent(RevisionListComponent);
    const selected = vi.fn();
    fixture.componentInstance.selected.subscribe(selected);
    fixture.componentRef.setInput('rows', [row]);
    fixture.componentRef.setInput('selectedId', row.id);
    fixture.detectChanges();
    const action = rootElement(fixture).querySelector<HTMLButtonElement>('[data-policy-select]');
    expect(action?.tagName).toBe('BUTTON');
    expect(action?.getAttribute('aria-pressed')).toBe('true');
    expect(action?.getAttribute('aria-controls')).toBe('policy-selected-detail');
    action?.click();
    expect(selected).toHaveBeenCalledExactlyOnceWith(row.id);
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    action?.click();
    expect(selected).toHaveBeenCalledTimes(1);
  });
});
