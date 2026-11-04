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
  });

  afterEach(() => vi.restoreAllMocks());

  it.each([
    ['default', undefined, 'pending', 'Not evaluated'],
    ['hover', undefined, 'pending', 'Not evaluated'],
    ['focus-visible', undefined, 'pending', 'Not evaluated'],
    ['active', undefined, 'pending', 'Not evaluated'],
    ['disabled', undefined, 'pending', 'Not evaluated'],
    ['loading', undefined, 'pending', 'Not evaluated'],
    ['empty', null, 'pending', 'Not evaluated'],
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
      const badge = root.querySelector('[hlmBadge]');
      expect(badge?.getAttribute('data-slot')).toBe('badge');
      expect(badge?.classList.contains(`bg-[var(--eg-${tone}-bg)]`)).toBe(true);
      expect(badge?.classList.contains(`text-[var(--eg-${tone}-fg)]`)).toBe(true);
      expect(root.querySelector('.verdict__label')?.textContent).toBe('Decision:');
      expect(root.querySelector('.verdict__value')?.textContent).toBe(label);
      expect(
        root.querySelector('[tabindex], [role], [aria-disabled], button, a, [aria-busy]'),
      ).toBeNull();
      expect(root.querySelector('svg, [data-icon="check"]')).toBeNull();
    },
  );

  it.each([
    [undefined, 'pending', 'Not evaluated'],
    [null, 'pending', 'Not evaluated'],
    ['pending', 'pending', 'Not evaluated'],
    ['unavailable', 'neutral', 'Decision unavailable'],
    ['unknown', 'neutral', 'Decision unavailable'],
    ['pass', 'neutral', 'Decision unavailable'],
    ['fail', 'neutral', 'Decision unavailable'],
    ['PASS ', 'neutral', 'Decision unavailable'],
    ['', 'neutral', 'Decision unavailable'],
    ['PASS FAIL', 'neutral', 'Decision unavailable'],
    ['run-123', 'neutral', 'Decision unavailable'],
    ['<img src=x onerror=alert(1)>', 'neutral', 'Decision unavailable'],
    ['x'.repeat(512), 'neutral', 'Decision unavailable'],
  ])('renders %s as %s with no release verdict', (value, tone, label) => {
    const fixture = TestBed.createComponent(DecisionChipComponent);
    fixture.componentRef.setInput('decision', value);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-tone]')?.getAttribute('data-tone')).toBe(tone);
    expect(root.querySelector('[data-tone="success"], [data-tone="danger"], img')).toBeNull();
    expect(root.querySelector('.verdict__value')?.textContent?.trim()).toBe(label);
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
    expect(root.querySelector('.verdict__value')?.textContent).toBe('Not evaluated');
    expect(root.innerHTML).not.toMatch(/PASS|FAIL/);
    expect(root.querySelector('[data-tone="success"]')).toBeNull();
  });
  it('explains a failed decision read without confusing it with no evaluation', async () => {
    const fixture = TestBed.createComponent(DecisionChipComponent);
    fixture.componentRef.setInput('decision', 'unavailable');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-tone="neutral"]')?.getAttribute('title')).toBe(
      'The decision could not be read. Try again.',
    );
    fixture.componentRef.setInput('decision', null);
    await fixture.whenStable();
    expect(root.querySelector('[title]')).toBeNull();
    expect(root.querySelector('.verdict__value')?.textContent).toBe('Not evaluated');
  });

  it('renders unavailable separately from pending and restores canonical pending', async () => {
    const fixture = TestBed.createComponent(DecisionChipComponent);
    fixture.componentRef.setInput('decision', 'pending');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const pendingValue = root.querySelector('.verdict__value');
    expect(pendingValue?.textContent).toBe('Not evaluated');

    fixture.componentRef.setInput('decision', 'unknown');
    await fixture.whenStable();
    const unavailableValue = root.querySelector('.verdict__value');
    expect(unavailableValue).not.toBe(pendingValue);
    expect(unavailableValue?.textContent?.trim()).toBe('Decision unavailable');
    expect(root.querySelector('[data-tone="neutral"]')).not.toBeNull();
    expect(root.innerHTML).not.toMatch(/PASS|FAIL|pending/);

    fixture.componentRef.setInput('decision', null);
    await fixture.whenStable();
    expect(root.querySelector('.verdict__value')).not.toBe(unavailableValue);
    expect(root.querySelector('.verdict__value')?.textContent).toBe('Not evaluated');
    expect(root.querySelector('[data-tone="pending"]')).not.toBeNull();
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
      expect(value.classList.contains('whitespace-normal')).toBe(true);
      expect(value.classList.contains('[overflow-wrap:anywhere]')).toBe(true);
    }
  });

  it('keeps unavailable decisions out of the tab order while allowing hover explanation and Escape', async () => {
    const fixture = TestBed.createComponent(DecisionChipComponent);
    fixture.componentRef.setInput('decision', 'unavailable');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const badge = root.querySelector<HTMLElement>('.verdict--decision');
    expect(root.querySelector('[tabindex]')).toBeNull();
    expect(badge?.getAttribute('title')).toBe(fixture.componentInstance.explanation());
    badge?.dispatchEvent(new PointerEvent('pointerenter', { pointerType: 'mouse' }));
    await vi.waitFor(() => expect(document.querySelector('[role="tooltip"]')).not.toBeNull());
    await fixture.whenStable();
    expect(document.querySelector('[role="tooltip"]')?.textContent).toContain(
      'The decision could not be read. Try again.',
    );
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(document.querySelector('[role="tooltip"]')).toBeNull();
    });
  });
});
