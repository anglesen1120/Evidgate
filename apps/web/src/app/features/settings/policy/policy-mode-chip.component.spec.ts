import { TestBed } from '@angular/core/testing';
import { PolicyModeChipComponent } from './policy-mode-chip.component';

describe('PolicyModeChipComponent', () => {
  afterEach(() => TestBed.resetTestingModule());
  it.each([
    ['enforcing', 'enforcing', 'mode-gating'],
    ['advisory', 'advisory', 'mode-advisory'],
    ['future-mode', 'future-mode', null],
    ['', 'unavailable', null],
    [null, 'unavailable', null],
    [undefined, 'unavailable', null],
  ])('renders %s as text without inferring a mode', (mode, dataMode, tone) => {
    const fixture = TestBed.createComponent(PolicyModeChipComponent);
    fixture.componentRef.setInput('mode', mode);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const chip = root.querySelector<HTMLElement>('[data-policy-mode]');
    expect(chip?.dataset['mode']).toBe(dataMode);
    expect(chip?.textContent).toMatch(/\S/);
    if (mode) expect(chip?.textContent).toContain(mode);
    expect(chip?.classList.contains('mode-gating')).toBe(tone === 'mode-gating');
    expect(chip?.classList.contains('mode-advisory')).toBe(tone === 'mode-advisory');
    expect(chip?.hasAttribute('tabindex')).toBe(false);
  });
  it('never advertises the empty string as a mode', () => {
    const fixture = TestBed.createComponent(PolicyModeChipComponent);
    fixture.componentRef.setInput('mode', '');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    // The empty string is `unavailable`, not "a mode whose name is empty": the attribute is the
    // token the e2e suite reads, and an empty one would read as a raw mode nobody declared.
    expect(root.querySelector('[data-policy-mode]')?.getAttribute('data-mode')).toBe('unavailable');
  });
  it('escapes an unknown raw mode', () => {
    const fixture = TestBed.createComponent(PolicyModeChipComponent);
    fixture.componentRef.setInput('mode', '<img src=x onerror=1>');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('<img src=x onerror=1>');
    expect(root.querySelector('img')).toBeNull();
  });
});
