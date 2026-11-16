import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { HashChipComponent } from './hash-chip.component';

const HASH = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef';

describe('HashChipComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [HashChipComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it.each([
    [12, '012345…abcdef'],
    [4, '01…ef'],
    [1, '0…f'],
    [4.9, '01…ef'],
    [0, HASH],
    [-1, HASH],
    [Number.NaN, HASH],
    [Number.POSITIVE_INFINITY, HASH],
    [HASH.length, HASH],
  ])(
    'splits budget %s between both ends without losing the original',
    async (abbreviate, visible) => {
      const fixture = TestBed.createComponent(HashChipComponent);
      fixture.componentRef.setInput('value', HASH);
      fixture.componentRef.setInput('abbreviate', abbreviate);
      await fixture.whenStable();
      const root = fixture.nativeElement as HTMLElement;
      const badge = root.querySelector('[hlmBadge]');
      expect(root.querySelector('.identity-chip__value')?.textContent).toBe(visible);
      expect(badge?.getAttribute('data-full-value')).toBe(HASH);
      expect(badge?.classList.contains('font-mono')).toBe(true);
      expect(badge?.classList.contains('min-h-[var(--eg-space-7)]')).toBe(true);
      if (visible !== HASH) {
        expect(badge?.querySelector('.sr-only')?.textContent).toBe(HASH);
        expect(badge?.querySelector('[popover]')?.textContent).toBe(HASH);
        expect(badge?.getAttribute('tabindex')).toBe('0');
      } else {
        expect(badge?.querySelector('[popover]')).toBeNull();
      }
      expect(root.querySelector('button')?.getAttribute('aria-label')).toBe('Copy hash');
      expect(root.querySelector('[title]')).toBeNull();
    },
  );

  it.each([null, undefined, '', '   '])(
    'has no copy/disclosure for missing hash %s',
    async (value) => {
      const fixture = TestBed.createComponent(HashChipComponent);
      fixture.componentRef.setInput('value', value);
      await fixture.whenStable();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.textContent?.trim()).toBe('Hash unavailable');
      expect(root.querySelector('[data-full-value], [popover], button')).toBeNull();
    },
  );

  it('copies the complete original with a contextual accessible action', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    const fixture = TestBed.createComponent(HashChipComponent);
    fixture.componentRef.setInput('value', HASH);
    fixture.componentRef.setInput('copyLabel', 'Copy policy hash');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const button = root.querySelector<HTMLButtonElement>('button');
    expect(button?.getAttribute('aria-label')).toBe('Copy policy hash');
    button?.click();
    await fixture.whenStable();
    expect(writeText).toHaveBeenCalledWith(HASH);
    expect(writeText).not.toHaveBeenCalledWith('012345…abcdef');
    expect(root.querySelector('[role="status"]')?.textContent).toContain('copied');
  });

  it('retains an untrusted full identifier as text and fits a shrinkable label', async () => {
    const fixture = TestBed.createComponent(HashChipComponent);
    const value = '<img src=x onerror=alert(1)>'.repeat(12);
    fixture.componentRef.setInput('value', value);
    fixture.componentRef.setInput('abbreviate', 0);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.identity-chip__value')?.textContent).toBe(value);
    expect(
      root.querySelector('.identity-chip__value')?.classList.contains('whitespace-nowrap'),
    ).toBe(true);
    expect(root.querySelector('img, [title]')).toBeNull();
    expect(root.querySelector('[data-full-value]')?.getAttribute('data-full-value')).toBe(value);
  });
});
