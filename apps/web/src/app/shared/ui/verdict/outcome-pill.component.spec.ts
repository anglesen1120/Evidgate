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
    ['loading', undefined, 'neutral', 'Outcome unavailable'],
    ['empty', null, 'neutral', 'Outcome unavailable'],
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
    expect(root.querySelector('.verdict__label')?.textContent).toBe('Outcome:');
    expect(root.querySelector('.verdict__value')?.textContent).toBe(label);
    expect(root.textContent).not.toMatch(/PASS|FAIL|Evidence complete/);
    expect(
      root.querySelector('[tabindex], [role], [aria-disabled], button, a, [aria-busy]'),
    ).toBeNull();
    expect(root.querySelector('svg, [data-icon="check"]')).toBeNull();
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
    expect(root.querySelector('[data-tone="success"], .success, svg')).toBeNull();
    expect(root.querySelector('.verdict__value')?.textContent).toBe('Outcome unavailable');
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

  it('retains the full outcome label in a wrapping value without clipping markup', () => {
    const fixture = TestBed.createComponent(OutcomePillComponent);
    fixture.componentRef.setInput('outcome', 'all_passed');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('span.verdict > span.verdict__value')?.textContent).toBe(
      'All tests passed',
    );
    expect(root.querySelector('[title], [hidden], [aria-hidden]')).toBeNull();
    const value = root.querySelector<HTMLElement>('.verdict__value');
    expect(value).not.toBeNull();
    if (value) {
      expect(value.classList.contains('whitespace-normal')).toBe(true);
      expect(value.classList.contains('[overflow-wrap:anywhere]')).toBe(true);
    }
  });
});
