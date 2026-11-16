// State sheet: docs/design/web/state-sheet.md#evidencebadge
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { EvidenceBadgeComponent } from './evidence-badge.component';

describe('EvidenceBadgeComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [EvidenceBadgeComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  afterEach(() => vi.restoreAllMocks());

  it.each([
    ['default', 'incomplete', 'warning', 'Incomplete'],
    ['hover', 'incomplete', 'warning', 'Incomplete'],
    ['focus-visible', 'incomplete', 'warning', 'Incomplete'],
    ['active', 'incomplete', 'warning', 'Incomplete'],
    ['disabled', 'incomplete', 'warning', 'Incomplete'],
    ['loading', undefined, 'neutral', 'Unavailable'],
    ['empty', null, 'neutral', 'Unavailable'],
    ['error', 'incomplete', 'warning', 'Incomplete'],
    ['success', 'complete', 'success', 'Complete'],
  ] as const)('keeps %s render-only and collection-specific', (_state, value, tone, label) => {
    const fixture = TestBed.createComponent(EvidenceBadgeComponent);
    fixture.componentRef.setInput('completeness', value);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const badge = root.querySelector('span.verdict');

    expect(badge?.getAttribute('data-tone')).toBe(tone);
    expect(badge?.getAttribute('data-slot')).toBe('badge');
    expect(badge?.classList.contains(`bg-[var(--eg-${tone}-bg)]`)).toBe(true);
    expect(badge?.classList.contains(`text-[var(--eg-${tone}-fg)]`)).toBe(true);
    expect(root.querySelector('.verdict__label')?.textContent?.trim()).toBe('Completeness:');
    expect(root.querySelector('.verdict__value')?.textContent).toBe(label);
    expect(root.textContent).not.toMatch(/PASS|FAIL|Passed|passed/);
    expect(
      root.querySelector('[tabindex], [role], [aria-disabled], button, a, [aria-busy]'),
    ).toBeNull();
    expect(badge?.querySelector('hlm-icon')?.getAttribute('aria-hidden')).toBe('true');
  });

  it.each([
    undefined,
    null,
    'unknown',
    'PASS',
    'FAIL',
    'complete ',
    'COMPLETE',
    '0f3a9c21',
    '<img src=x onerror=alert(1)>',
    'x'.repeat(512),
  ])('never promotes unavailable or unrecognized completeness (%s)', (value) => {
    const fixture = TestBed.createComponent(EvidenceBadgeComponent);
    fixture.componentRef.setInput('completeness', value);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-tone="neutral"]')).not.toBeNull();
    expect(root.querySelector('[data-tone="success"], .success, img')).toBeNull();
    expect(root.querySelector('.verdict__value')?.textContent).toBe('Unavailable');
    // Identifiers are not completeness values: do not echo or reinterpret them as a verdict.
    expect(root.textContent).not.toMatch(/PASS|FAIL|0f3a9c21|onerror/);
  });

  it('updates from supplied complete to missing without retaining success', async () => {
    const fixture = TestBed.createComponent(EvidenceBadgeComponent);
    fixture.componentRef.setInput('completeness', 'complete');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-tone="success"]')).not.toBeNull();

    fixture.componentRef.setInput('completeness', undefined);
    await fixture.whenStable();
    expect(root.querySelector('[data-tone="success"]')).toBeNull();
    expect(root.querySelector('.verdict__value')?.textContent).toBe('Unavailable');
  });

  it('keeps complete explanations outside the short single-line badge', async () => {
    const fixture = TestBed.createComponent(EvidenceBadgeComponent);
    fixture.componentRef.setInput('showDetail', true);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const value = root.querySelector('.verdict__value');
    expect(value?.textContent).toBe('Unavailable');
    expect(value?.classList.contains('whitespace-nowrap')).toBe(true);
    expect(root.querySelector('[hlmBadge] .verdict__detail')).toBeNull();
    expect(root.querySelector('.verdict__detail')?.textContent?.trim()).toBe(
      'Evidence status unavailable',
    );
    expect(fixture.componentInstance.detail()).toBe('Evidence status unavailable');
    expect(root.querySelector('[title], [popover]')).toBeNull();
  });

  it('preserves unknown provenance outside the neutral chip as escaped text', async () => {
    const fixture = TestBed.createComponent(EvidenceBadgeComponent);
    fixture.componentRef.setInput('completeness', '<img src=x onerror=alert(1)>');
    fixture.componentRef.setInput('showDetail', true);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.verdict__value')?.textContent).toBe('Unavailable');
    expect(root.querySelector('.verdict__detail')?.textContent).toContain('<img src=x');
    expect(fixture.componentInstance.tone()).toBe('neutral');
    expect(root.querySelector('img, [data-tone="success"]')).toBeNull();
    const dimension = root.querySelector('.verdict__label')!;
    expect(dimension.classList.contains('sr-only')).toBe(false);
    expect(root.querySelector('[hlmBadge]')?.contains(dimension)).toBe(false);
    fixture.componentRef.setInput('compact', true);
    await fixture.whenStable();
    expect(dimension.classList.contains('sr-only')).toBe(true);
  });
});
