// State sheet: docs/design/web/state-sheet.md#waiver-banner
import { registerLocaleData } from '@angular/common';
import viLocale from '@angular/common/locales/vi';
import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  provideZonelessChangeDetection,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { WaiverBanner } from './waiver-banner.component';

const states = [
  'default',
  'hover',
  'focus-visible',
  'active',
  'disabled',
  'loading',
  'empty',
  'error',
  'success',
] as const;
const expiry = '2026-10-24T17:30:00-07:00';

@Component({
  selector: 'app-waiver-test-host',
  standalone: true,
  imports: [WaiverBanner],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="stored-outcome">{{ outcome }}</p>
    <p class="stored-decision">{{ decision }}</p>
    <app-waiver-banner [approver]="'Reviewer-001'" [expiresAt]="expiry" />
  `,
})
class WaiverTestHost {
  readonly outcome = 'has_failures';
  readonly decision = 'pending';
  readonly expiry = expiry;
}

describe('WaiverBanner (FR-09-2/7)', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WaiverBanner, WaiverTestHost],
      providers: [provideZonelessChangeDetection(), { provide: LOCALE_ID, useValue: 'en-US' }],
    }).compileComponents();
    vi.spyOn(globalThis, 'fetch');
  });

  afterEach(() => vi.restoreAllMocks());

  it.each(states)('covers the %s state as static waiver metadata', (state) => {
    const fixture = TestBed.createComponent(WaiverBanner);
    fixture.componentRef.setInput(
      'approver',
      state === 'empty' ? '' : 'reviewer-001 <script>not markup</script>',
    );
    fixture.componentRef.setInput('expiresAt', state === 'empty' ? '' : expiry);
    // "error" is a supplied revoked domain variant, not a request failure.
    fixture.componentRef.setInput('state', state === 'error' ? 'revoked' : 'active');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('section')?.getAttribute('aria-label')).toBe('Waiver details');
    expect(root.querySelector('script')).toBeNull();
    expect(root.textContent).toContain(
      'This waiver does not change the stored outcome or decision.',
    );
    if (state === 'empty') {
      expect(root.textContent).toContain('Approver unavailable');
      expect(root.textContent).toContain('Expiry unavailable');
      expect(root.querySelector('time')).toBeNull();
      expect(root.querySelector('h3')?.textContent).toContain('Waiver details unavailable');
      expect(root.textContent).not.toContain('Approved by waiver');
    } else {
      expect(root.querySelector('.waiver-approver')?.textContent?.trim()).toBe(
        'reviewer-001 <script>not markup</script>',
      );
      expect(root.querySelector('time')?.getAttribute('datetime')).toBe('2026-10-25T00:30:00.000Z');
      expect(root.querySelector('time')?.textContent).toBe('Oct 25, 2026, 12:30:00\u202fAM');
      expect(root.querySelector('.waiver-utc')?.textContent).toBe('2026-10-25T00:30:00.000Z');
    }
    if (state === 'error') {
      expect(root.querySelector('.waiver-revoked')).not.toBeNull();
      expect(root.querySelector('h3')?.textContent).toContain('Waiver revoked');
    }
    expect(
      root.querySelector('[tabindex], [role="button"], [aria-disabled], button, a'),
    ).toBeNull();
    expect(
      root.querySelector('[role="status"], [aria-live], [aria-busy="true"], [class*="success"]'),
    ).toBeNull();
    expect(root.textContent).not.toMatch(/\b(PASS|FAIL|complete)\b/);
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });

  it('keeps the stored outcome and pending decision beside the waiver, unchanged', () => {
    const fixture = TestBed.createComponent(WaiverTestHost);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const outcome = root.querySelector('.stored-outcome');
    const decision = root.querySelector('.stored-decision');
    expect(outcome?.textContent).toBe('has_failures');
    expect(decision?.textContent).toBe('pending');
    expect(root.querySelector('app-waiver-banner')).not.toBeNull();
    fixture.detectChanges();
    expect(root.querySelector('.stored-outcome')).toBe(outcome);
    expect(root.querySelector('.stored-decision')).toBe(decision);
    expect(root.textContent).not.toMatch(/\b(PASS|FAIL)\b/);
  });

  it('renders supplied expired state without deriving it from the clock', () => {
    const fixture = TestBed.createComponent(WaiverBanner);
    fixture.componentRef.setInput('approver', 'Reviewer-001');
    fixture.componentRef.setInput('expiresAt', new Date('2099-01-01T00:00:00Z'));
    fixture.componentRef.setInput('state', 'expired');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.waiver-expired')).not.toBeNull();
    expect(root.querySelector('h3')?.textContent).toContain('Waiver expired');
    expect(root.querySelector('time')?.getAttribute('datetime')).toBe('2099-01-01T00:00:00.000Z');
  });

  it.each(['2026-10-25T00:30:00', '2026-10-25T00:30:00Z'])(
    'renders %s in UTC independently of the device timezone',
    (value) => {
      const fixture = TestBed.createComponent(WaiverBanner);
      fixture.componentRef.setInput('approver', 'Reviewer-001');
      fixture.componentRef.setInput('expiresAt', value);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelector('time')?.getAttribute('datetime')).toBe('2026-10-25T00:30:00.000Z');
      expect(root.querySelector('time')?.textContent).toBe('Oct 25, 2026, 12:30:00\u202fAM');
    },
  );

  it('preserves a long approver in the wrapping metadata structure', () => {
    const fixture = TestBed.createComponent(WaiverBanner);
    const approver = 'Người phê duyệt miễn trừ '.repeat(30);
    fixture.componentRef.setInput('approver', approver);
    fixture.componentRef.setInput('expiresAt', expiry);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.waiver-approver')?.textContent?.trim()).toBe(approver.trim());
    expect(root.querySelector('.waiver dl')).not.toBeNull();
    expect(root.querySelector('[title]')).toBeNull();
  });
});

describe('WaiverBanner locale', () => {
  it('formats the absolute UTC expiry using the active Vietnamese locale', async () => {
    registerLocaleData(viLocale);
    await TestBed.configureTestingModule({
      imports: [WaiverBanner],
      providers: [provideZonelessChangeDetection(), { provide: LOCALE_ID, useValue: 'vi' }],
    }).compileComponents();
    const fixture = TestBed.createComponent(WaiverBanner);
    fixture.componentRef.setInput('approver', 'Reviewer-001');
    fixture.componentRef.setInput('expiresAt', expiry);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('time')?.textContent).toBe('00:30:00 25 thg 10, 2026');
    expect(root.querySelector('time')?.getAttribute('datetime')).toBe('2026-10-25T00:30:00.000Z');
    expect(root.querySelector('.waiver-utc')?.textContent).toBe('2026-10-25T00:30:00.000Z');
  });
});
