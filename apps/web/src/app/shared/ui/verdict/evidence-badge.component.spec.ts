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
    vi.spyOn(globalThis, 'fetch');
  });

  afterEach(() => vi.restoreAllMocks());

  it.each([
    ['default', 'incomplete', 'warning', 'Evidence incomplete'],
    ['hover', 'incomplete', 'warning', 'Evidence incomplete'],
    ['focus-visible', 'incomplete', 'warning', 'Evidence incomplete'],
    ['active', 'incomplete', 'warning', 'Evidence incomplete'],
    ['disabled', 'incomplete', 'warning', 'Evidence incomplete'],
    ['loading', undefined, 'neutral', 'Evidence status unavailable'],
    ['empty', null, 'neutral', 'Evidence status unavailable'],
    ['error', 'incomplete', 'warning', 'Evidence incomplete'],
    ['success', 'complete', 'success', 'Evidence complete'],
  ] as const)('keeps %s render-only and collection-specific', (_state, value, tone, label) => {
    const fixture = TestBed.createComponent(EvidenceBadgeComponent);
    fixture.componentRef.setInput('completeness', value);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const badge = root.querySelector('span.verdict');

    expect(badge?.getAttribute('data-tone')).toBe(tone);
    expect(root.querySelector('.verdict__label')?.textContent).toBe('Completeness:');
    expect(root.querySelector('.verdict__value')?.textContent).toBe(label);
    expect(root.textContent).not.toMatch(/PASS|FAIL|Passed|passed/);
    expect(
      root.querySelector('[tabindex], [role], [aria-disabled], button, a, [aria-busy]'),
    ).toBeNull();
    expect(root.querySelector('svg, [data-icon="check"]')).toBeNull();
    expect(globalThis.fetch).not.toHaveBeenCalled();
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
    expect(root.querySelector('[data-tone="success"], .success, svg, img')).toBeNull();
    expect(root.querySelector('.verdict__value')?.textContent).toBe('Evidence status unavailable');
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
    expect(root.querySelector('.verdict__value')?.textContent).toBe('Evidence status unavailable');
  });

  it('keeps the full collection copy in a wrapping value, without title-only content', () => {
    const fixture = TestBed.createComponent(EvidenceBadgeComponent);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('span.verdict > span.verdict__value')?.textContent).toBe(
      'Evidence status unavailable',
    );
    expect(root.querySelector('[title], [hidden], [aria-hidden]')).toBeNull();
    const value = root.querySelector<HTMLElement>('.verdict__value');
    expect(value).not.toBeNull();
    if (value) {
      expect(getComputedStyle(value).whiteSpace).toBe('normal');
      expect(getComputedStyle(value).overflowWrap).toBe('anywhere');
    }
  });
});
