import { TestBed } from '@angular/core/testing';
import { RotationReminderChipComponent } from './rotation-reminder-chip.component';

describe('RotationReminderChipComponent', () => {
  afterEach(() => TestBed.resetTestingModule());
  const now = new Date('2026-10-25T00:00:00Z');
  it.each([
    ['2026-11-08T00:00:00.000Z', 'expires-soon', 'Expires soon'],
    ['2026-11-08T00:00:00.001Z', null, null],
    ['2026-10-25T00:00:00.000Z', 'expired', 'Expired'],
    ['2026-10-24T23:59:59.999Z', 'expired', 'Expired'],
    [null, null, null],
  ])('renders the fixed-clock boundary %s as %s', (expires_at, reminder, label) => {
    const fixture = TestBed.createComponent(RotationReminderChipComponent);
    fixture.componentRef.setInput('reference', { status: 'active', expires_at });
    fixture.componentRef.setInput('now', now);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-reminder]')?.getAttribute('data-reminder') ?? null).toBe(
      reminder,
    );
    if (label) {
      expect(root.textContent).toContain(label);
      expect(root.querySelector('[aria-hidden="true"]')?.textContent).toBeTruthy();
    } else expect(root.textContent?.trim()).toBe('');
  });
  it('does not invent a reminder for non-active metadata', () => {
    const fixture = TestBed.createComponent(RotationReminderChipComponent);
    fixture.componentRef.setInput('reference', {
      status: 'revoked',
      expires_at: now.toISOString(),
    });
    fixture.componentRef.setInput('now', now);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-reminder]')).toBeNull();
  });
});
