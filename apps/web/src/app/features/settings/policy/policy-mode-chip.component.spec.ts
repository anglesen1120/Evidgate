import { TestBed } from '@angular/core/testing';
import { PolicyModeChipComponent } from './policy-mode-chip.component';
import { rootElement } from './policy-reads.test-support';

describe('PolicyModeChipComponent', () => {
  afterEach(() => TestBed.resetTestingModule());
  it.each([
    ['enforcing', 'enforcing', 'bg-[var(--eg-info-bg)]'],
    ['advisory', 'advisory', 'bg-[var(--eg-warning-bg)]'],
    ['future-mode', 'future-mode', 'bg-[var(--eg-neutral-bg)]'],
    ['', 'unavailable', 'bg-[var(--eg-neutral-bg)]'],
    [null, 'unavailable', 'bg-[var(--eg-neutral-bg)]'],
    [undefined, 'unavailable', 'bg-[var(--eg-neutral-bg)]'],
  ] as const)('renders %s as text without inferring a mode', (mode, dataMode, tone) => {
    const fixture = TestBed.createComponent(PolicyModeChipComponent);
    fixture.componentRef.setInput('mode', mode);
    fixture.detectChanges();
    const root = rootElement(fixture);
    const chip = root.querySelector<HTMLElement>('[data-policy-mode]');
    expect(chip?.dataset['mode']).toBe(dataMode);
    expect(chip?.textContent).toMatch(/\S/);
    if (mode) expect(chip?.textContent).toContain(mode);
    expect(chip?.classList.contains(tone)).toBe(true);
    expect(chip?.getAttribute('data-slot')).toBe('badge');
    expect(chip?.classList.contains('whitespace-normal')).toBe(true);
    expect(chip?.hasAttribute('tabindex')).toBe(false);
  });
  it('never advertises the empty string as a mode', () => {
    const fixture = TestBed.createComponent(PolicyModeChipComponent);
    fixture.componentRef.setInput('mode', '');
    fixture.detectChanges();
    const root = rootElement(fixture);
    // The empty string is `unavailable`, not "a mode whose name is empty": the attribute is the
    // token the e2e suite reads, and an empty one would read as a raw mode nobody declared.
    expect(root.querySelector('[data-policy-mode]')?.getAttribute('data-mode')).toBe('unavailable');
  });
  it('escapes an unknown raw mode', () => {
    const fixture = TestBed.createComponent(PolicyModeChipComponent);
    fixture.componentRef.setInput('mode', '<img src=x onerror=1>');
    fixture.detectChanges();
    const root = rootElement(fixture);
    expect(root.textContent).toContain('<img src=x onerror=1>');
    expect(root.querySelector('img')).toBeNull();
  });
});
