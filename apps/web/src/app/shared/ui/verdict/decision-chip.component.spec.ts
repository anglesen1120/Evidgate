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
      expect(root.querySelector('.verdict__label')?.textContent?.trim()).toBe('Decision:');
      expect(root.querySelector('.verdict__value')?.textContent).toBe(label);
      expect(
        root.querySelector('[tabindex], [role], [aria-disabled], button, a, [aria-busy]'),
      ).toBeNull();
      expect(badge?.querySelector('hlm-icon')?.getAttribute('aria-hidden')).toBe('true');
    },
  );

  it.each([
    [undefined, 'pending', 'Not evaluated'],
    [null, 'pending', 'Not evaluated'],
    ['pending', 'pending', 'Not evaluated'],
    ['unavailable', 'neutral', 'Unavailable'],
    ['unknown', 'neutral', 'Unavailable'],
    ['pass', 'neutral', 'Unavailable'],
    ['fail', 'neutral', 'Unavailable'],
    ['PASS ', 'neutral', 'Unavailable'],
    ['', 'neutral', 'Unavailable'],
    ['PASS FAIL', 'neutral', 'Unavailable'],
    ['run-123', 'neutral', 'Unavailable'],
    ['<img src=x onerror=alert(1)>', 'neutral', 'Unavailable'],
    ['x'.repeat(512), 'neutral', 'Unavailable'],
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
    expect(fixture.componentInstance.detail()).toBe('The decision could not be read. Try again.');
    expect(root.querySelector('.verdict__detail')?.textContent?.trim()).toBe(
      'The decision could not be read. Try again.',
    );
    expect(root.querySelector('[hlmBadge] .verdict__detail')).toBeNull();
    expect(root.querySelector('[title], [popover]')).toBeNull();
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
    expect(unavailableValue?.textContent?.trim()).toBe('Unavailable');
    expect(root.querySelector('[data-tone="neutral"]')).not.toBeNull();
    expect(root.innerHTML).not.toMatch(/PASS|FAIL|pending/);

    fixture.componentRef.setInput('decision', null);
    await fixture.whenStable();
    expect(root.querySelector('.verdict__value')).not.toBe(unavailableValue);
    expect(root.querySelector('.verdict__value')?.textContent).toBe('Not evaluated');
    expect(root.querySelector('[data-tone="pending"]')).not.toBeNull();
  });

  it('keeps the longest canonical verdict on one line with a decorative warning icon', () => {
    const fixture = TestBed.createComponent(DecisionChipComponent);
    fixture.componentRef.setInput('decision', 'INCONCLUSIVE');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const value = root.querySelector('.verdict__value');
    expect(value?.textContent).toBe('INCONCLUSIVE');
    expect(value?.classList.contains('whitespace-nowrap')).toBe(true);
    expect(root.querySelector('[title], [popover]')).toBeNull();
    expect(root.querySelector('[hlmBadge] .verdict__label')).toBeNull();
    expect(root.querySelector('hlm-icon')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('renders read-failure detail outside the badge, not as an unconditional tooltip', async () => {
    const fixture = TestBed.createComponent(DecisionChipComponent);
    fixture.componentRef.setInput('decision', 'unavailable');
    fixture.componentRef.setInput('showDetail', true);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.verdict__detail')?.textContent?.trim()).toBe(
      'The decision could not be read. Try again.',
    );
    expect(root.querySelector('[hlmBadge] .verdict__detail')).toBeNull();
    expect(root.querySelector('[title], [popover], [tabindex]')).toBeNull();
    fixture.componentRef.setInput('decision', 'pending');
    await fixture.whenStable();
    expect(root.querySelector('.verdict__value')?.textContent).toBe('Not evaluated');
    expect(root.querySelector('.verdict__detail')?.textContent?.trim()).toBe('Not evaluated');
    expect(root.querySelector('hlm-icon svg path')?.getAttribute('d')).toBe('M12 6v6l4 2');
    expect(root.querySelector('.animate-spin')).toBeNull();
  });

  it('preserves unknown release provenance only in the external escaped detail', async () => {
    const fixture = TestBed.createComponent(DecisionChipComponent);
    fixture.componentRef.setInput('decision', '<img src=x onerror=alert(1)>');
    fixture.componentRef.setInput('showDetail', true);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.verdict__value')?.textContent?.trim()).toBe('Unavailable');
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
