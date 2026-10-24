// State sheet: docs/design/web/state-sheet.md#decisionchip
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { DecisionChipComponent } from './decision-chip.component';

describe('DecisionChipComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DecisionChipComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    vi.spyOn(globalThis, 'fetch');
  });

  afterEach(() => vi.restoreAllMocks());

  it.each([
    ['default', undefined, 'pending', 'pending'],
    ['hover', undefined, 'pending', 'pending'],
    ['focus-visible', undefined, 'pending', 'pending'],
    ['active', undefined, 'pending', 'pending'],
    ['disabled', undefined, 'pending', 'pending'],
    ['loading', undefined, 'pending', 'pending'],
    ['empty', null, 'pending', 'pending'],
    ['error', 'FAIL', 'danger', 'FAIL'],
    ['success', 'PASS', 'success', 'PASS'],
  ] as const)(
    'keeps %s render-only and renders only the supplied decision',
    (_state, value, tone, label) => {
      const fixture = TestBed.createComponent(DecisionChipComponent);
      fixture.componentRef.setInput('decision', value);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;

      expect(root.querySelector('span.verdict')?.getAttribute('data-tone')).toBe(tone);
      expect(root.querySelector('.verdict__label')?.textContent).toBe('Decision:');
      expect(root.querySelector('.verdict__value')?.textContent).toBe(label);
      expect(
        root.querySelector('[tabindex], [role], [aria-disabled], button, a, [aria-busy]'),
      ).toBeNull();
      expect(root.querySelector('svg, [data-icon="check"]')).toBeNull();
      expect(globalThis.fetch).not.toHaveBeenCalled();
    },
  );

  it.each([
    undefined,
    null,
    'pending',
    'unknown',
    'pass',
    'fail',
    'PASS ',
    'PASS FAIL',
    'run-123',
    '<img src=x onerror=alert(1)>',
    'x'.repeat(512),
  ])('renders canonical pending without any verdict for %s', (value) => {
    const fixture = TestBed.createComponent(DecisionChipComponent);
    fixture.componentRef.setInput('decision', value);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-tone="pending"]')).not.toBeNull();
    expect(
      root.querySelector('[data-tone="success"], [data-tone="danger"], .success, svg, img'),
    ).toBeNull();
    expect(root.querySelector('.verdict__value')?.textContent).toBe('pending');
    for (const node of [root, ...root.querySelectorAll('*')]) {
      expect(node.textContent).not.toMatch(/PASS|FAIL/);
      expect(node.getAttribute('aria-label') ?? '').not.toMatch(/PASS|FAIL/);
      expect(node.getAttribute('title') ?? '').not.toMatch(/PASS|FAIL/);
    }
  });

  it.each([
    ['PASS', 'success'],
    ['FAIL', 'danger'],
    ['INCONCLUSIVE', 'warning'],
  ] as const)('preserves locale-independent domain value %s verbatim', (value, tone) => {
    const fixture = TestBed.createComponent(DecisionChipComponent);
    fixture.componentRef.setInput('decision', value);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('.verdict__value')?.textContent).toBe(value);
    expect(root.querySelector('[data-tone]')?.getAttribute('data-tone')).toBe(tone);
    if (value !== 'PASS') {
      expect(root.textContent).not.toContain('PASS');
      expect(root.querySelector('[data-tone="success"]')).toBeNull();
    }
  });

  it('removes a prior PASS from all nodes when the supplied decision becomes pending', async () => {
    const fixture = TestBed.createComponent(DecisionChipComponent);
    fixture.componentRef.setInput('decision', 'PASS');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.verdict__value')?.textContent).toBe('PASS');

    fixture.componentRef.setInput('decision', null);
    await fixture.whenStable();
    expect(root.querySelector('.verdict__value')?.textContent).toBe('pending');
    expect(root.innerHTML).not.toMatch(/PASS|FAIL/);
    expect(root.querySelector('[data-tone="success"]')).toBeNull();
  });

  it('keeps the longest verdict whole in the wrapping value structure', () => {
    const fixture = TestBed.createComponent(DecisionChipComponent);
    fixture.componentRef.setInput('decision', 'INCONCLUSIVE');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('span.verdict > span.verdict__value')?.textContent).toBe(
      'INCONCLUSIVE',
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
