// State sheet: docs/design/web/state-sheet.md#status-pills
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

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
      expect(root.querySelector('.status-pill__dimension')?.textContent).toBe('Run status:');
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
    expect(root.textContent?.trim()).toBe('future_state');
    expect(root.querySelector('.status-pill--neutral')).not.toBeNull();
  });

  it.each(['future_state', '__proto__', 'constructor', '<img src=x onerror=alert(1)>'])(
    'preserves unknown value %s verbatim with a neutral tone',
    (state) => {
      expect(isRunState(state)).toBe(false);
      const fixture = TestBed.createComponent(RunStatePillComponent);
      fixture.componentRef.setInput('state', state);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelector('.status-pill__label')?.textContent).toBe(state);
      expect(root.querySelector('.status-pill--neutral')).not.toBeNull();
      expect(root.querySelector('.status-pill--success, .status-pill--danger, img')).toBeNull();
    },
  );

  it.each([null, undefined, '', '   '])(
    'does not infer a lifecycle value for missing %s',
    (state) => {
      const fixture = TestBed.createComponent(RunStatePillComponent);
      fixture.componentRef.setInput('state', state);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelector('.status-pill__label')?.textContent).toBe('Run status unavailable');
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
    expect(root.querySelector('.status-pill__label')?.textContent).toBe('future_state');
  });
});
