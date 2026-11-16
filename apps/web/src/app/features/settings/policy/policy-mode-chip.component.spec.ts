import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { PolicyModeChipComponent } from './policy-mode-chip.component';
import { rootElement } from './policy-reads.test-support';

describe('PolicyModeChipComponent', () => {
  afterEach(() => TestBed.resetTestingModule());
  it.each([
    ['enforcing', 'enforcing', 'info'],
    ['advisory', 'advisory', 'warning'],
    ['future-mode', 'future-mode', 'neutral'],
    ['', 'unavailable', 'neutral'],
    [null, 'unavailable', 'neutral'],
    [undefined, 'unavailable', 'neutral'],
  ] as const)('renders %s as text without inferring a mode', (mode, dataMode, tone) => {
    const fixture = TestBed.createComponent(PolicyModeChipComponent);
    fixture.componentRef.setInput('mode', mode);
    fixture.detectChanges();
    const root = rootElement(fixture);
    const chip = root.querySelector<HTMLElement>('[data-policy-mode]');
    expect(chip?.dataset['mode']).toBe(dataMode);
    expect(chip?.textContent).toMatch(/\S/);
    if (mode) expect(chip?.textContent).toContain(mode);
    const pill = fixture.debugElement.query(By.directive(StatusPillComponent))
      .componentInstance as StatusPillComponent;
    expect(pill.tone()).toBe(tone);
    expect(pill.label()).toMatch(/\S/);
    expect(root.querySelector('app-status-pill svg')).not.toBeNull();
    expect(root.querySelector('[data-policy-mode-raw]')?.textContent ?? '').toBe(mode ?? '');
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
