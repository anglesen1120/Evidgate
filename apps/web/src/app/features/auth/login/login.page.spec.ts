// State sheet: docs/design/web/state-sheet.md#shared-accessibility-and-themes
import { provideZonelessChangeDetection, signal, type WritableSignal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { vi, type Mock } from 'vitest';

import { AuthFlow, LOCATION_NAVIGATE, type LoginProvider, type LoginReason } from '../auth-flow';
import { LoginPage } from './login.page';
import { LOGIN_EXTENSION_LOADER } from './login-extension';

describe('LoginPage', () => {
  let fixture: ComponentFixture<LoginPage>;
  let root: HTMLElement;
  const navigate = vi.fn<(url: string) => void>();
  const router = { navigated: false };
  let flow: {
    returnUrl: WritableSignal<string>;
    reason: WritableSignal<LoginReason>;
    redirecting: WritableSignal<boolean>;
    start: Mock<(provider: LoginProvider) => void>;
  };

  const buttons = () =>
    Array.from(root.querySelectorAll<HTMLButtonElement>('.login-providers button'));
  const notice = () => root.querySelector<HTMLElement>('[role="status"]')!;
  const heading = () => root.querySelector<HTMLElement>('#login-heading')!;

  beforeEach(async () => {
    router.navigated = false;
    navigate.mockReset();
    vi.spyOn(document, 'baseURI', 'get').mockReturnValue('https://evidgate.test/en/');
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
      providers: [
        provideZonelessChangeDetection(),
        { provide: LOGIN_EXTENSION_LOADER, useValue: () => Promise.resolve(null) },
        { provide: AuthFlow, useValue: flow },
        { provide: Router, useValue: router },
        { provide: LOCATION_NAVIGATE, useValue: navigate },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(LoginPage);
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => {
    document.documentElement.removeAttribute('data-theme');
    localStorage.removeItem('evidgate-theme');
    vi.restoreAllMocks();
  });

  it('separates branded preferences, introduction, provider card and private destination context', () => {
    expect(root.querySelector('.login-topbar .login-brand')?.textContent).toContain('Evidgate');
    expect(root.querySelector('.login-intro h1')).toBe(heading());
    expect(root.querySelector('.login-card')?.getAttribute('aria-labelledby')).toBe(
      'provider-heading',
    );
    expect(root.querySelector('.login-card .primary-provider')).toBe(buttons()[0]);
    expect(root.querySelector('.login-destination')?.textContent).toContain(
      fixture.componentInstance.savedDestination,
    );
    expect(root.querySelector('main')?.children[0]?.className).toBe('login-intro');
    expect(root.querySelector('main')?.children[1]?.className).toBe('login-card');
    expect(root.querySelector('main')?.children[2]?.className).toBe('login-destination');
    flow.returnUrl.set('/');
    fixture.detectChanges();
    expect(root.querySelector('.login-destination')?.textContent).toContain(
      fixture.componentInstance.defaultDestination,
    );
    expect(root.querySelector('.login-destination')?.textContent).not.toContain(
      fixture.componentInstance.savedDestination,
    );
  });

  it('changes theme through the shared service without starting authentication', () => {
    const theme = root.querySelector<HTMLButtonElement>('.theme-toggle');
    const before = theme?.getAttribute('aria-pressed');
    theme?.click();
    fixture.detectChanges();
    expect(theme?.getAttribute('aria-pressed')).not.toBe(before);
    expect(flow.start).not.toHaveBeenCalled();
  });

  it('switches localized bundles while preserving validated return context and notice', () => {
    flow.reason.set('expired');
    root.querySelector<HTMLButtonElement>('.locale-toggle')?.click();
    expect(navigate).toHaveBeenCalledOnce();
    const destination = new URL(navigate.mock.calls[0]?.[0] ?? '', document.baseURI);
    expect(destination.pathname).toBe('/vi/login');
    expect(destination.searchParams.get('returnUrl')).toBe(flow.returnUrl());
    expect(destination.searchParams.get('reason')).toBe('expired');
    expect(flow.start).not.toHaveBeenCalled();
    expect(root.innerHTML).not.toContain('never-render');
  });

  it('does not change locale while provider navigation is pending', () => {
    flow.redirecting.set(true);
    fixture.detectChanges();
    const locale = root.querySelector<HTMLButtonElement>('.locale-toggle');
    expect(locale?.disabled).toBe(true);
    locale?.click();
    expect(navigate).not.toHaveBeenCalled();
  });

  it('includes the visible language label in the accessible name', () => {
    const locale = root.querySelector<HTMLButtonElement>('.locale-toggle');
    const visibleLabel = locale?.textContent?.replace(/\s+/g, ' ').trim();
    expect(visibleLabel).toBe('EN / VI');
    expect(locale?.getAttribute('aria-label')).toMatch(/^EN \/ VI:/);
  });

  it.each(['/', '/garden/', '/preview/', '/enrichment/'])(
    'hides language switching for the unlocalized base %s',
    (path) => {
      vi.spyOn(document, 'baseURI', 'get').mockReturnValue(`https://evidgate.test${path}`);
      const unlocalized = TestBed.createComponent(LoginPage);
      unlocalized.detectChanges();
      expect((unlocalized.nativeElement as HTMLElement).querySelector('.locale-toggle')).toBeNull();
      unlocalized.componentInstance.switchLocale();
      expect(navigate).not.toHaveBeenCalled();
      unlocalized.destroy();
    },
  );

  it('preserves the deployment prefix when switching a localized bundle', () => {
    vi.spyOn(document, 'baseURI', 'get').mockReturnValue('https://evidgate.test/review/en/');
    const nested = TestBed.createComponent(LoginPage);
    nested.detectChanges();
    (nested.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('.locale-toggle')
      ?.click();
    expect(navigate).toHaveBeenCalledOnce();
    expect(new URL(navigate.mock.calls[0]?.[0] ?? '', document.baseURI).pathname).toBe(
      '/review/vi/login',
    );
    nested.destroy();
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

  it('entry (F-LOGIN-01): leaves the browser’s own focus start alone on a page load', () => {
    // Preferences precede provider actions; the heading remains outside sequential focus order.
    expect(document.activeElement).toBe(document.body);
    expect(heading().getAttribute('tabindex')).toBe('-1');
    expect(heading().tabIndex).toBe(-1);
    expect(heading().textContent?.trim()).toBe(fixture.componentInstance.heading);
  });

  it('entry (F-LOGIN-01): focuses the heading when the visit came from inside the app', async () => {
    // No shell renders around this page, so its own handoff is the only one a client-side entry
    // gets: the callback's failed/unavailable redirect and the store's expiry redirect land here.
    router.navigated = true;
    const revisit = TestBed.createComponent(LoginPage);
    revisit.detectChanges();
    await revisit.whenStable();

    const landed = (revisit.nativeElement as HTMLElement).querySelector('#login-heading')!;
    expect(document.activeElement).toBe(landed);
    expect(landed.getAttribute('tabindex')).toBe('-1');
    expect(landed.textContent?.trim()).toBe(fixture.componentInstance.heading);
  });

  it('redirecting (F-LOGIN-02): moves focus off the disabling provider onto the heading', async () => {
    const google = buttons()[0]!;
    google.focus();
    expect(document.activeElement).toBe(google);

    flow.redirecting.set(true);
    fixture.detectChanges();
    await fixture.whenStable();

    // Both providers are unfocusable now, so leaving focus on the button would leave the page with
    // no focus and no ring at all while it is still on screen.
    expect(buttons().every((button) => button.disabled)).toBe(true);
    expect(document.activeElement).toBe(heading());
    expect(document.activeElement).not.toBe(google);
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
