// State sheet: docs/design/web/state-sheet.md#how-the-specs-link-here
// 09.1.1-T7 AC8: independent verdict fields; AC9: zoneless gallery regression coverage.
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { UiGalleryComponent } from './ui-gallery.component';

describe('UiGalleryComponent (AC8 / AC9)', () => {
  let root: HTMLElement;
  let fixture: ComponentFixture<UiGalleryComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [UiGalleryComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(UiGalleryComponent);
    await fixture.whenStable();
    root = fixture.nativeElement as HTMLElement;
  });

  it('keeps the pending evaluation in three separate component elements', () => {
    const row = root.querySelector('[data-demo="evaluation-pending"]');
    expect(row).not.toBeNull();
    expect(Array.from(row?.children ?? []).map((element) => element.tagName.toLowerCase())).toEqual(
      ['app-evidence-badge', 'app-outcome-pill', 'app-decision-chip'],
    );
    expect(row?.querySelector('app-evidence-badge')?.textContent).toContain('Evidence incomplete');
    expect(row?.querySelector('app-outcome-pill')?.textContent).toContain('No results');
    expect(row?.querySelector('app-decision-chip')?.textContent).toContain('pending');
  });

  it('never paints incomplete evidence and a pending decision as success', () => {
    const row = root.querySelector('[data-demo="evaluation-pending"]');
    expect(row).not.toBeNull();
    expect(row?.querySelector('[data-tone="success"], [class*="success"]')).toBeNull();
    expect(row?.querySelector('app-evidence-badge [data-tone]')?.getAttribute('data-tone')).toBe(
      'warning',
    );
    expect(row?.querySelector('app-decision-chip [data-tone]')?.getAttribute('data-tone')).toBe(
      'pending',
    );
    expect(row?.textContent).not.toContain('PASS');
  });

  it('shows literal PASS only in the explicitly supplied PASS chip specimen', () => {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    const matches: Node[] = [];
    while (walker.nextNode()) {
      if (/\bPASS\b/.test(walker.currentNode.textContent ?? '')) matches.push(walker.currentNode);
    }
    expect(matches).toHaveLength(1);
    expect(matches[0]?.parentElement?.closest('[data-demo="decision-pass"]')).not.toBeNull();
    expect(
      root.querySelector('[data-demo="evaluation-fail"] app-decision-chip')?.textContent,
    ).toContain('FAIL');
  });

  it('renders every component group and both regular and virtual typed table fixtures', () => {
    expect(root.querySelectorAll('h1')).toHaveLength(1);
    expect(root.querySelectorAll('main > section > h2')).toHaveLength(16);
    expect(root.querySelectorAll('app-run-state-pill')).toHaveLength(8);
    expect(
      root
        .querySelector('[data-demo=\"table-small\"] [role=\"table\"]')
        ?.getAttribute('aria-rowcount'),
    ).toBe('13');
    expect(
      root.querySelectorAll('[data-demo="table-small"] .body-table [role="row"]'),
    ).toHaveLength(12);
    expect(
      root
        .querySelector('[data-demo=\"table-virtual\"] [role=\"table\"]')
        ?.getAttribute('aria-rowcount'),
    ).toBe('251');
    expect(
      root.querySelector('[data-demo="table-virtual"] cdk-virtual-scroll-viewport'),
    ).not.toBeNull();
    expect(
      root
        .querySelector('[data-demo=\"table-empty\"] [role=\"table\"]')
        ?.getAttribute('aria-rowcount'),
    ).toBe('1');
  });

  it('includes every request pattern and both native retry eligibility states', async () => {
    for (const selector of [
      'app-loading-pattern',
      'app-empty-pattern',
      'app-error-pattern',
      'app-forbidden-pattern',
      'app-not-found-pattern',
      'app-conflict-dialog',
      'app-request-state app-empty-pattern',
    ]) {
      expect(root.querySelector(selector), selector).not.toBeNull();
    }
    expect(
      root.querySelector<HTMLButtonElement>('[data-demo="retry-after-waiting"] button')?.disabled,
    ).toBe(true);
    expect(
      root.querySelector<HTMLButtonElement>('[data-demo="retry-after-eligible"] button')?.disabled,
    ).toBe(false);
    expect(root.querySelector('app-request-state')?.textContent).not.toContain('Current content');
    const status = root.querySelector('[data-demo="retry-count"]');
    expect(status?.textContent).toContain('0');
    root.querySelector<HTMLButtonElement>('[data-demo="retry-after-eligible"] button')?.click();
    await fixture.whenStable();
    expect(status?.textContent).toContain('1');
    // Local example event only; it does not issue an API call.
    expect(
      root.querySelector('[data-demo="retry-after-waiting"] button')?.hasAttribute('disabled'),
    ).toBe(true);
  });
});
