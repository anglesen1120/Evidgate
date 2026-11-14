// State sheet: docs/design/web/state-sheet.md#status-pills
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { HlmBadge } from '@evidgate/ui/badge';

import { RunStatePillComponent } from './run-state-pill.component';
import { isRunState, RUN_STATE_TONES, type RunState } from './run-state-tone';
import type { StatusTone } from './status-pill.component';

const STATES = [
  ['created', 'neutral', 'Created'],
  ['queued', 'neutral', 'Queued'],
  ['running', 'info', 'Running'],
  ['collecting', 'info', 'Collecting'],
  ['complete', 'success', 'Complete'],
  ['incomplete', 'warning', 'Incomplete'],
  ['canceled', 'neutral', 'Canceled'],
] as const satisfies readonly (readonly [RunState, StatusTone, string])[];

describe('RunStatePillComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RunStatePillComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  it('maps exactly the seven canonical lifecycle states without danger or inferred success', () => {
    expect(RUN_STATE_TONES).toEqual({
      created: 'neutral',
      queued: 'neutral',
      running: 'info',
      collecting: 'info',
      complete: 'success',
      incomplete: 'warning',
      canceled: 'neutral',
    });
    expect(Object.keys(RUN_STATE_TONES).sort()).toEqual(STATES.map(([state]) => state).sort());
    expect(Object.values(RUN_STATE_TONES)).not.toContain('danger');
    expect(Object.entries(RUN_STATE_TONES).filter(([, tone]) => tone === 'success')).toEqual([
      ['complete', 'success'],
    ]);
  });

  it.each(STATES)(
    'renders %s as %s collection lifecycle, never a release verdict',
    (state, tone, label) => {
      expect(isRunState(state)).toBe(true);
      const fixture = TestBed.createComponent(RunStatePillComponent);
      fixture.componentRef.setInput('state', state);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelector('.status-pill__label')?.textContent).toBe(label);
      expect(root.querySelector('.status-pill__dimension')?.textContent?.trim()).toBe(
        'Run status:',
      );
      expect(root.querySelector(`.status-pill--${tone}`)).not.toBeNull();
      const badge = root.querySelector('[hlmBadge]');
      expect(badge?.getAttribute('data-slot')).toBe('badge');
      expect(badge?.classList.contains(`bg-[var(--eg-${tone}-bg)]`)).toBe(true);
      expect(root.querySelector('[tabindex], [role], [aria-disabled], button, a')).toBeNull();
      expect(root.innerHTML).not.toMatch(/\b(?:PASS|FAIL)\b/);
      expect(root.querySelector('.decision-chip, [class*="decision"]')).toBeNull();
    },
  );

  it('omits only the redundant dimension in compact tables and preserves status semantics', () => {
    const fixture = TestBed.createComponent(RunStatePillComponent);
    fixture.componentRef.setInput('state', 'complete');
    fixture.componentRef.setInput('compact', true);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent?.trim()).toBe('Complete');
    expect(root.querySelector('.status-pill__dimension')).toBeNull();
    expect(root.querySelector('.status-pill--success')).not.toBeNull();
    fixture.componentRef.setInput('state', 'future_state');
    fixture.detectChanges();
    expect(root.querySelector('.status-pill__label')?.textContent).toBe('Unknown');
    expect(root.querySelector('.status-pill--neutral')).not.toBeNull();
  });

  it.each(['future_state', '__proto__', 'constructor', '<img src=x onerror=alert(1)>'])(
    'preserves unknown value %s outside a short neutral badge',
    (state) => {
      expect(isRunState(state)).toBe(false);
      const fixture = TestBed.createComponent(RunStatePillComponent);
      fixture.componentRef.setInput('state', state);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelector('.status-pill__label')?.textContent).toBe('Unknown');
      expect(root.querySelector('.status-pill__detail')?.textContent).toContain(state);
      expect(fixture.componentInstance.detail()).toContain(state);
      expect(root.querySelector('.status-pill--neutral')).not.toBeNull();
      expect(root.querySelector('.status-pill--success, .status-pill--danger, img')).toBeNull();
    },
  );

  it('exposes bidi-safe unknown provenance with only the default inputs', async () => {
    const prefix = 'trạng_thái_lượt_chạy_chưa_từng_được_nhận_diện_ở_máy_chủ_';
    const state = `${prefix}\u202e<script>\u{e0001}`;
    const complete = `Unknown run status: ${prefix}⟨U+202E⟩<script>⟨U+E0001⟩`;
    const fixture = TestBed.createComponent(RunStatePillComponent);
    fixture.componentRef.setInput('state', state);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const badgeElement = fixture.debugElement.query(By.directive(HlmBadge));
    if (!badgeElement) {
      throw new Error('Expected the lifecycle badge');
    }
    expect(fixture.componentInstance.showDetail()).toBe(false);
    expect(fixture.componentInstance.compact()).toBe(false);
    expect(fixture.componentInstance.state()).toBe(state);
    expect(root.querySelector('.status-pill__label')?.textContent).toBe('Unknown');
    expect(root.querySelector('.status-pill--neutral')).not.toBeNull();
    expect(root.querySelector('.status-pill__detail bdi')?.textContent).toBe(complete);
    expect(badgeElement.injector.get(HlmBadge).fullValue()).toBe(complete);
    expect(root.querySelector('[hlmBadge] .sr-only')?.textContent).toBe(complete);
    expect(root.querySelector('.spartan-badge__full-value')?.textContent?.trim()).toBe(complete);
    expect(root.textContent).not.toMatch(/\p{Cf}/u);
    expect(
      root.querySelector('script, img, .status-pill--success, .status-pill--danger'),
    ).toBeNull();
  });

  it.each([null, undefined, '', '   '])(
    'does not infer a lifecycle value for missing %s',
    (state) => {
      const fixture = TestBed.createComponent(RunStatePillComponent);
      fixture.componentRef.setInput('state', state);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelector('.status-pill__label')?.textContent).toBe('Unavailable');
      expect(fixture.componentInstance.detail()).toBe('Run status unavailable');
      expect(root.querySelector('.status-pill--neutral')).not.toBeNull();
      expect(root.innerHTML).not.toMatch(/\b(?:PASS|FAIL|Complete)\b/);
    },
  );

  it('replaces complete styling when the supplied state changes to unknown', async () => {
    const fixture = TestBed.createComponent(RunStatePillComponent);
    fixture.componentRef.setInput('state', 'complete');
    await fixture.whenStable();
    fixture.componentRef.setInput('state', 'future_state');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.status-pill--success')).toBeNull();
    expect(root.querySelector('.status-pill--neutral')).not.toBeNull();
    expect(root.querySelector('.status-pill__label')?.textContent).toBe('Unknown');
    expect(root.querySelector('.status-pill__detail')?.textContent).toContain('future_state');
    fixture.componentRef.setInput('state', 'complete');
    await fixture.whenStable();
    expect(root.querySelector('.status-pill--neutral')).toBeNull();
    expect(root.querySelector('.status-pill__label')?.textContent).toBe('Complete');
    expect(root.querySelector('.status-pill__detail, .spartan-badge__full-value')).toBeNull();
  });
});
