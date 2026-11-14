// State sheet: docs/design/web/state-sheet.md#status-pills
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { OutcomePillComponent } from './outcome-pill.component';

describe('OutcomePillComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [OutcomePillComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  afterEach(() => vi.restoreAllMocks());

  it.each([
    ['default', 'no_results', 'neutral', 'No results'],
    ['hover', 'no_results', 'neutral', 'No results'],
    ['focus-visible', 'no_results', 'neutral', 'No results'],
    ['active', 'no_results', 'neutral', 'No results'],
    ['disabled', 'no_results', 'neutral', 'No results'],
    ['loading', undefined, 'neutral', 'Unavailable'],
    ['empty', null, 'neutral', 'Unavailable'],
    ['error', 'has_failures', 'danger', 'Has failures'],
    ['success', 'all_passed', 'success', 'All tests passed'],
  ] as const)('keeps %s render-only and outcome-specific', (_state, value, tone, label) => {
    const fixture = TestBed.createComponent(OutcomePillComponent);
    fixture.componentRef.setInput('outcome', value);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('span.verdict')?.getAttribute('data-tone')).toBe(tone);
    const badge = root.querySelector('[hlmBadge]');
    expect(badge?.getAttribute('data-slot')).toBe('badge');
    expect(badge?.classList.contains(`bg-[var(--eg-${tone}-bg)]`)).toBe(true);
    expect(badge?.classList.contains(`text-[var(--eg-${tone}-fg)]`)).toBe(true);
    expect(root.querySelector('.verdict__label')?.textContent?.trim()).toBe('Outcome:');
    expect(root.querySelector('.verdict__value')?.textContent).toBe(label);
    expect(root.textContent).not.toMatch(/PASS|FAIL|Evidence complete/);
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
    'all_passed ',
    'ALL_PASSED',
    'run-123',
    '<svg onload=alert(1)>',
    'x'.repeat(512),
  ])('does not infer a test outcome from %s', (value) => {
    const fixture = TestBed.createComponent(OutcomePillComponent);
    fixture.componentRef.setInput('outcome', value);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-tone="neutral"]')).not.toBeNull();
    expect(root.querySelector('[data-tone="success"], .success, svg[onload]')).toBeNull();
    expect(root.querySelector('.verdict__value')?.textContent).toBe('Unavailable');
    expect(root.textContent).not.toMatch(/PASS|FAIL|run-123|onload/);
  });

  it('replaces a previously passing outcome with neutral zero-results data zonelessly', async () => {
    const fixture = TestBed.createComponent(OutcomePillComponent);
    fixture.componentRef.setInput('outcome', 'all_passed');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-tone="success"]')).not.toBeNull();

    fixture.componentRef.setInput('outcome', 'no_results');
    await fixture.whenStable();
    expect(root.querySelector('[data-tone="success"], .success')).toBeNull();
    expect(root.querySelector('.verdict__value')?.textContent).toBe('No results');
  });

  it('keeps the full explanation outside the short single-line value', async () => {
    const fixture = TestBed.createComponent(OutcomePillComponent);
    fixture.componentRef.setInput('outcome', 'all_passed');
    fixture.componentRef.setInput('showDetail', true);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const value = root.querySelector('.verdict__value');
    expect(value?.textContent).toBe('All tests passed');
    expect(value?.classList.contains('whitespace-nowrap')).toBe(true);
    expect(root.querySelector('[hlmBadge] .verdict__detail')).toBeNull();
    expect(root.querySelector('.verdict__detail')?.textContent?.trim()).toBe('All tests passed');
    expect(root.querySelector('[title], [popover]')).toBeNull();
  });

  it('preserves unknown outcome provenance outside the neutral chip', async () => {
    const fixture = TestBed.createComponent(OutcomePillComponent);
    fixture.componentRef.setInput('outcome', '<img src=x onerror=alert(1)>');
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
