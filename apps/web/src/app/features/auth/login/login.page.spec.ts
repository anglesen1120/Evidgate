// State sheet: docs/design/web/state-sheet.md#shared-accessibility-and-themes
import { provideZonelessChangeDetection, signal, type WritableSignal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { vi, type Mock } from 'vitest';

import { AuthFlow, type LoginProvider, type LoginReason } from '../auth-flow';
import { LoginPage } from './login.page';

describe('LoginPage', () => {
  let fixture: ComponentFixture<LoginPage>;
  let root: HTMLElement;
  let flow: {
    returnUrl: WritableSignal<string>;
    reason: WritableSignal<LoginReason>;
    redirecting: WritableSignal<boolean>;
    start: Mock<(provider: LoginProvider) => void>;
  };

  const buttons = () => Array.from(root.querySelectorAll('button'));
  const notice = () => root.querySelector<HTMLElement>('[role="status"]')!;

  beforeEach(async () => {
    flow = {
      returnUrl: signal('/private-target?secret=never-render'),
      reason: signal<LoginReason>('none'),
      redirecting: signal(false),
      start: vi.fn<(provider: LoginProvider) => void>(),
    };
    // The page declares `providers: [AuthFlow]` (the real flow), which shadows a TestBed provider:
    // the fake is installed on the component itself so these cases stay about the template.
    TestBed.overrideComponent(LoginPage, {
      set: { providers: [{ provide: AuthFlow, useValue: flow }] },
    });
    await TestBed.configureTestingModule({
      imports: [LoginPage],
      providers: [provideZonelessChangeDetection(), { provide: AuthFlow, useValue: flow }],
    }).compileComponents();
    fixture = TestBed.createComponent(LoginPage);
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  });

  it('default: renders one named main, one h1 and provider-only sign-in', () => {
    expect(root.querySelectorAll('main')).toHaveLength(1);
    expect(root.querySelectorAll('h1')).toHaveLength(1);
    expect(root.querySelector('main')?.getAttribute('aria-labelledby')).toBe('login-heading');
    expect(root.querySelector('h1')?.textContent).toBe(fixture.componentInstance.heading);
    expect(buttons().map((button) => button.textContent?.trim())).toEqual([
      fixture.componentInstance.google,
      fixture.componentInstance.github,
    ]);
    expect(buttons().every((button) => button.type === 'button' && !button.disabled)).toBe(true);
    expect(root.querySelector('input, form, a')).toBeNull();
    expect(root.textContent).not.toContain('private-target');
    expect(root.innerHTML).not.toContain('never-render');
    expect(notice().textContent?.trim()).toBe('');
    expect(flow.start).not.toHaveBeenCalled();
  });

  it('hover: does not start sign-in or alter the notice merely on pointer entry', () => {
    buttons()[0]?.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
    expect(flow.start).not.toHaveBeenCalled();
    expect(notice().textContent?.trim()).toBe('');
  });

  it('focus-visible: keeps both native buttons focusable in document order without signing in', () => {
    const [google, github] = buttons() as [HTMLButtonElement, HTMLButtonElement];
    google.focus();
    expect(document.activeElement).toBe(google);
    github.focus();
    expect(document.activeElement).toBe(github);
    expect(buttons().every((button) => button.tabIndex === 0)).toBe(true);
    expect(root.querySelector('section')?.hasAttribute('tabindex')).toBe(false);
    expect(flow.start).not.toHaveBeenCalled();
  });

  it('active: delegates each provider activation to AuthFlow and nothing else', () => {
    buttons()[0]?.click();
    buttons()[1]?.click();
    expect(flow.start.mock.calls).toEqual([['google'], ['github']]);
    expect(flow.reason()).toBe('none');
    expect(flow.returnUrl()).toBe('/private-target?secret=never-render');
    expect(notice().textContent?.trim()).toBe('');
  });

  it('disabled: blocks both provider buttons while the browser is redirecting', () => {
    flow.redirecting.set(true);
    fixture.detectChanges();
    for (const button of buttons()) {
      expect(button.disabled).toBe(true);
      button.click();
    }
    expect(flow.start).not.toHaveBeenCalled();
  });

  it('loading: announces redirection outside the busy controls and replaces a stale failure', () => {
    flow.reason.set('failed');
    fixture.detectChanges();
    const liveRegion = notice();
    flow.redirecting.set(true);
    fixture.detectChanges();
    expect(notice()).toBe(liveRegion);
    expect(notice().textContent?.trim()).toBe(fixture.componentInstance.redirecting);
    expect(notice().getAttribute('aria-live')).toBe('polite');
    expect(notice().getAttribute('aria-atomic')).toBe('true');
    const busy = root.querySelector('[aria-busy="true"]')!;
    expect(busy.contains(notice())).toBe(false);
    expect(notice().classList.contains('login-notice--error')).toBe(false);
    expect(root.textContent).not.toContain(fixture.componentInstance.failed);
  });

  it('empty: has no artificial empty collection state and retains the sign-in choices', () => {
    expect(buttons()).toHaveLength(2);
    expect(root.querySelector('[class*="empty"], [role="progressbar"]')).toBeNull();
    expect(notice().textContent?.trim()).toBe('');
  });

  it.each(['failed', 'unavailable'] as const)(
    'error (%s): announces the problem and retries through the same provider choices',
    (reason) => {
      flow.reason.set(reason);
      fixture.detectChanges();
      expect(notice().textContent?.trim()).toBe(fixture.componentInstance[reason]);
      expect(notice().classList.contains('login-notice--error')).toBe(true);
      expect(notice().getAttribute('aria-live')).toBe('polite');
      expect(buttons().every((button) => !button.disabled)).toBe(true);
      buttons()[0]?.click();
      buttons()[1]?.click();
      expect(flow.start.mock.calls).toEqual([['google'], ['github']]);
    },
  );

  it('success: confirms tab-only sign-out without claiming the server session ended', () => {
    flow.reason.set('signed-out');
    fixture.detectChanges();
    const copy = fixture.componentInstance;
    expect(notice().textContent).toContain(copy.signedOut);
    expect(notice().textContent).toContain(copy.signedOutDetail);
    expect(notice().classList.contains('login-notice--success')).toBe(true);
    expect(buttons().every((button) => !button.disabled)).toBe(true);
    expect(notice().textContent).not.toMatch(/session has ended|signed out everywhere/i);
  });

  it('expired: explains that reauthentication is needed and keeps both providers available', () => {
    flow.reason.set('expired');
    fixture.detectChanges();
    expect(notice().textContent?.trim()).toBe(fixture.componentInstance.expired);
    expect(notice().classList.contains('login-notice--info')).toBe(true);
    expect(notice().getAttribute('aria-live')).toBe('polite');
    expect(buttons().every((button) => !button.disabled)).toBe(true);
  });

  it('clears a previous notice and busy state when AuthFlow returns to idle', () => {
    flow.reason.set('unavailable');
    flow.redirecting.set(true);
    fixture.detectChanges();
    flow.reason.set('none');
    flow.redirecting.set(false);
    fixture.detectChanges();
    expect(notice().textContent?.trim()).toBe('');
    expect(root.querySelector('[aria-busy="true"]')).toBeNull();
    expect(buttons().every((button) => !button.disabled)).toBe(true);
  });
});
