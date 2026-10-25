import { TestBed } from '@angular/core/testing';
import { PolicyModeChipComponent } from './policy-mode-chip.component';

describe('PolicyModeChipComponent', () => {
  afterEach(() => TestBed.resetTestingModule());
  it.each([
    ['enforcing', 'mode-gating'],
    ['advisory', 'mode-advisory'],
    ['future-mode', null],
    [null, null],
  ])('renders %s as text without inferring a mode', (mode, tone) => {
    const fixture = TestBed.createComponent(PolicyModeChipComponent);
    fixture.componentRef.setInput('mode', mode);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const chip = root.querySelector<HTMLElement>('[data-policy-mode]');
    expect(chip?.dataset['mode']).toBe(mode ?? 'unavailable');
    expect(chip?.textContent).toMatch(/\S/);
    if (mode) expect(chip?.textContent).toContain(mode);
    expect(chip?.classList.contains('mode-gating')).toBe(tone === 'mode-gating');
    expect(chip?.classList.contains('mode-advisory')).toBe(tone === 'mode-advisory');
    expect(chip?.hasAttribute('tabindex')).toBe(false);
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
