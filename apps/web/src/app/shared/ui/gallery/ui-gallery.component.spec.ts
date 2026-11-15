// State sheet: docs/design/web/state-sheet.md#how-the-specs-link-here
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
    expect(row?.querySelector('app-evidence-badge')?.textContent).toMatch(
      /Completeness:\s*Incomplete/,
    );
    expect(row?.querySelector('app-outcome-pill')?.textContent).toMatch(/Outcome:\s*No results/);
    expect(row?.querySelector('app-decision-chip')?.textContent).toMatch(
      /Decision:\s*Not evaluated/,
    );
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
    expect(root.querySelectorAll('main > section > h2')).toHaveLength(17);
    expect(root.querySelectorAll('app-run-state-pill')).toHaveLength(8);
    expect(
      root
        .querySelector('[data-demo=\"table-small\"] [role=\"table\"]')
        ?.getAttribute('aria-rowcount'),
    ).toBe('13');
    expect(
      root.querySelectorAll('[data-demo="table-small"] .body-table [data-row-id]'),
    ).toHaveLength(12);
    expect(
      root.querySelectorAll('[data-demo="table-small"] .body-table [aria-rowindex="1"]'),
    ).toHaveLength(1);
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
    // The column-role specimen uses the shared table too: one component, roles and budgets, no
    // hand-built table that can hide a column at a narrow width.
    expect(root.querySelectorAll('app-data-table')).toHaveLength(4);
    expect(
      root.querySelectorAll('[data-demo="gallery-column-roles"] tbody tr[data-row-id]'),
    ).toHaveLength(2);
    expect(root.querySelector('[data-demo="gallery-column-roles"] app-hash-chip')).not.toBeNull();
    expect(root.querySelector('[data-demo="gallery-column-roles"] app-status-pill')).not.toBeNull();
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

  it('demonstrates the one-time reveal through its unchanged public interface', async () => {
    root.querySelector<HTMLButtonElement>('[data-demo="reveal-opener"]')?.click();
    await fixture.whenStable();
    const control = root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]');
    expect(control?.value).toBe('example-value-not-a-credential');
    expect(control?.readOnly).toBe(true);
    root.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]')?.click();
    await fixture.whenStable();
    expect(root.querySelector('app-connect-step-reveal')).toBeNull();
    expect(root.querySelector('[data-demo="reveal-opener"]')).not.toBeNull();
    expect(document.activeElement).toBe(root.querySelector('[data-demo="reveal-opener"]'));
  });

  it('opens the menu with ArrowDown, navigates both ways and restores focus on Escape', async () => {
    const trigger = root.querySelector<HTMLButtonElement>('[data-demo="examples-menu"]');
    if (!trigger) throw new Error('Expected examples menu trigger');
    trigger.focus();
    trigger.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowDown', keyCode: 40, bubbles: true }),
    );
    await fixture.whenStable();
    const items = document.querySelectorAll<HTMLElement>('[role="menuitem"]');
    expect(items).toHaveLength(2);
    expect(trigger.getAttribute('aria-expanded')).toBe('true');
    expect(document.activeElement).toBe(items[0]);
    items[0]?.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowDown', keyCode: 40, bubbles: true }),
    );
    expect(document.activeElement).toBe(items[1]);
    items[1]?.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowUp', keyCode: 38, bubbles: true }),
    );
    expect(document.activeElement).toBe(items[0]);
    items[0]?.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
    );
    await fixture.whenStable();
    expect(document.querySelector('[role="menu"]')).toBeNull();
    expect(document.activeElement).toBe(trigger);
    expect(trigger.getAttribute('aria-expanded')).toBe('false');
  });

  it('activates the conflict menu item with Enter without a pointer click', async () => {
    const trigger = root.querySelector<HTMLButtonElement>('[data-demo="examples-menu"]');
    if (!trigger) throw new Error('Expected examples menu trigger');
    trigger.focus();
    trigger.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowDown', keyCode: 40, bubbles: true }),
    );
    await fixture.whenStable();
    const item = document.querySelector<HTMLElement>('[role="menuitem"]');
    if (!item) throw new Error('Expected conflict menu item');
    item.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', keyCode: 13, bubbles: true }));
    await fixture.whenStable();
    expect(document.querySelector('[role="menu"]')).toBeNull();
    expect(document.querySelector('[role="dialog"]')?.textContent).toContain('Review conflict');
  });
});
