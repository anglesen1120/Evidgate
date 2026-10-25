import { LOCALE_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { PolicyRevisionSummaryDto } from '@evidgate/sdk';
import { RevisionListComponent } from './revision-list.component';

const row: PolicyRevisionSummaryDto = {
  id: '3e496610-072b-4d48-a789-6d423838c603',
  revision_no: 3,
  hash: 'a123456789abcdef0123456789abcdef',
  state: 'active',
  created_by: 'actor',
  created_at: '2026-10-26T06:00:00Z',
  version: 1,
};
describe('RevisionListComponent', () => {
  afterEach(() => TestBed.resetTestingModule());
  it('renders revision metadata, raw state, localized creation time and head marker', () => {
    TestBed.configureTestingModule({ providers: [{ provide: LOCALE_ID, useValue: 'vi' }] });
    const fixture = TestBed.createComponent(RevisionListComponent);
    fixture.componentRef.setInput('rows', [row]);
    fixture.componentRef.setInput('headId', row.id);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const revision = root.querySelector<HTMLElement>('[data-policy-revision]');
    expect(revision?.dataset['revisionNo']).toBe('3');
    expect(revision?.dataset['revisionState']).toBe(row.state);
    expect(revision?.hasAttribute('data-policy-head')).toBe(true);
    expect(revision?.textContent).toContain(row.state);
    expect(revision?.textContent).toContain(row.hash.slice(0, 12));
    expect(root.querySelector('time')?.textContent).toBe(
      new Intl.DateTimeFormat('vi', {
        dateStyle: 'medium',
        timeStyle: 'short',
      }).format(new Date(row.created_at)),
    );
  });
  it('shows a labelled empty state and bounded load-more action only when available', () => {
    const fixture = TestBed.createComponent(RevisionListComponent);
    const more = vi.fn();
    fixture.componentInstance.loadMore.subscribe(more);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
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
});
