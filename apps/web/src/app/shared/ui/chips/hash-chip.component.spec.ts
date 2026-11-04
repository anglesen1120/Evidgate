// State sheet: docs/design/web/state-sheet.md#hash-chip
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

  afterEach(() => vi.restoreAllMocks());

  it.each([
    'default',
    'hover',
    'focus-visible',
    'active',
    'disabled',
    'loading',
    'empty',
    'error',
    'success',
  ])('keeps the %s state render-only without a request/verdict transition', (state) => {
    const fetch = vi.spyOn(globalThis, 'fetch');
    const fixture = TestBed.createComponent(HashChipComponent);
    fixture.componentRef.setInput('value', state === 'empty' ? '' : HASH);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[hlmBadge]')?.getAttribute('data-slot')).toBe('badge');
    expect(root.querySelector('[hlmBadge]')?.classList.contains('font-mono')).toBe(true);
    expect(
      root.querySelector('[tabindex], [role], [aria-disabled], [aria-busy], button, a'),
    ).toBeNull();
    expect(root.querySelector('[title], svg')).toBeNull();
    expect(root.textContent).not.toMatch(/\b(?:PASS|FAIL|Complete)\b/);
    expect(root.querySelector('[class*="success"], [class*="danger"]')).toBeNull();
    expect(fetch).not.toHaveBeenCalled();
    if (state === 'empty') {
      expect(root.textContent?.trim()).toBe('Hash unavailable');
      expect(root.querySelector('[data-full-value]')).toBeNull();
    } else {
      expect(root.querySelector('.identity-chip__value')?.textContent).toBe('0123456789ab…');
      expect(root.querySelector('[data-full-value]')?.getAttribute('data-full-value')).toBe(HASH);
      const fullValue = root.querySelector('.hash-chip__full-value');
      expect(fullValue?.textContent).toBe(HASH);
      expect(fullValue?.classList.contains('sr-only')).toBe(true);
      expect(fullValue?.closest('[aria-hidden="true"], [hidden]')).toBeNull();
      expect(root.querySelector('.identity-chip__value')?.getAttribute('aria-hidden')).toBe('true');
    }
  });

  it.each([null, undefined, '', '   '])('makes absent hash %s explicit', (value) => {
    const fixture = TestBed.createComponent(HashChipComponent);
    fixture.componentRef.setInput('value', value);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent?.trim()).toBe('Hash unavailable');
    expect(root.querySelector('[data-full-value], .hash-chip__full-value')).toBeNull();
  });

  it.each([
    [12, '0123456789ab…'],
    [4, '0123…'],
    [1, '0…'],
    [4.9, '0123…'],
    [0, HASH],
    [-1, HASH],
    [Number.NaN, HASH],
    [Number.POSITIVE_INFINITY, HASH],
    [HASH.length, HASH],
    [HASH.length + 1, HASH],
  ])('uses abbreviation count %s without losing the full hash', (abbreviate, visible) => {
    const fixture = TestBed.createComponent(HashChipComponent);
    fixture.componentRef.setInput('value', HASH);
    fixture.componentRef.setInput('abbreviate', abbreviate);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.identity-chip__value')?.textContent).toBe(visible);
    expect(root.querySelector('[data-full-value]')?.getAttribute('data-full-value')).toBe(HASH);
    if (visible === HASH) {
      expect(root.querySelector('[aria-hidden], .hash-chip__full-value')).toBeNull();
    }
  });

  it.each(['a', '0123456789ab', 'mã-định-danh'])('keeps short identity %s verbatim', (value) => {
    const fixture = TestBed.createComponent(HashChipComponent);
    fixture.componentRef.setInput('value', value);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent?.trim()).toBe(value);
    expect(root.querySelector('.hash-chip__full-value')).toBeNull();
  });

  it('uses the wrapping value structure for long full identifiers without rendering markup', () => {
    const fixture = TestBed.createComponent(HashChipComponent);
    const value = '<img src=x onerror=alert(1)>'.repeat(24);
    fixture.componentRef.setInput('value', value);
    fixture.componentRef.setInput('abbreviate', 0);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.hash-chip .identity-chip__value')?.textContent).toBe(value);
    expect(root.querySelector('img, [title]')).toBeNull();
    expect(root.querySelector('[data-full-value]')?.getAttribute('data-full-value')).toBe(value);
  });

  it('replaces unavailable copy when the supplied hash arrives without fetching', async () => {
    const fixture = TestBed.createComponent(HashChipComponent);
    fixture.componentRef.setInput('value', '');
    await fixture.whenStable();
    fixture.componentRef.setInput('value', HASH);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).not.toContain('Hash unavailable');
    expect(root.querySelector('.hash-chip__full-value')?.textContent).toBe(HASH);
  });
});
