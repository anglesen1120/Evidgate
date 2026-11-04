import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { InviteRevealComponent } from './invite-reveal.component';

describe('InviteRevealComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), RevealSlot] }),
  );
  afterEach(() => TestBed.resetTestingModule());

  it('delegates the raw value, accessible labels and copy/dismiss controls to the shared reveal', () => {
    TestBed.inject(RevealSlot).show({ source: 'invite-token', value: 'raw-invite-not-a-url' });
    const fixture = TestBed.createComponent(InviteRevealComponent);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const control = root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]')!;
    expect(control.value).toBe('raw-invite-not-a-url');
    expect(root.textContent).toContain('Invite token');
    expect(root.textContent).toContain('Shown once.');
    expect(
      root.querySelector(`#${control.getAttribute('aria-labelledby')}`)?.textContent,
    ).toContain('Invite token');
    expect(
      root.querySelector(`#${control.getAttribute('aria-describedby')}`)?.textContent,
    ).toContain('needs it to accept');
    const dismissed = vi.fn();
    fixture.componentInstance.dismissed.subscribe(dismissed);
    root.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]')!.click();
    expect(dismissed).toHaveBeenCalledTimes(1);
  });
  it('renders an actionable replay notice rather than a reveal when no entry exists', () => {
    const fixture = TestBed.createComponent(InviteRevealComponent);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('app-connect-step-reveal')).toBeNull();
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Use Resend');
    expect(root.querySelector('h2')?.getAttribute('tabindex')).toBe('-1');
    expect(root.querySelector('button[hlmBtn]')?.getAttribute('type')).toBe('button');
    const dismissed = vi.fn();
    fixture.componentInstance.dismissed.subscribe(dismissed);
    root.querySelector<HTMLButtonElement>('button')!.click();
    expect(dismissed).toHaveBeenCalledTimes(1);
  });
});
