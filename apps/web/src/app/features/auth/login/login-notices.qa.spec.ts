import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { APP_BASE_HREF } from '@angular/common';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { SessionStore } from '../../../core/session/session.store';
import { LOCATION_NAVIGATE } from '../auth-flow';
import { LOGIN_EXTENSION_LOADER } from './login-extension';
import { LoginPage } from './login.page';

class LoginNoticeHarness extends ComponentHarness {
  static hostSelector = 'app-login-page';
  readonly notice = this.locatorFor('.login-notice');
  readonly providers = this.locatorForAll('.login-providers button');
  readonly primary = this.locatorFor('.primary-provider');
  readonly destination = this.locatorFor('.login-destination');
}

describe('Login notice boundaries with the real auth flow', () => {
  async function open(reason: string | null, signedOut = false) {
    const signedOutState = signal(signedOut);
    const navigate = vi.fn<(url: string) => void>();
    await TestBed.configureTestingModule({
      imports: [LoginPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: APP_BASE_HREF, useValue: '/en/' },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              queryParamMap: convertToParamMap({
                ...(reason === null ? {} : { reason }),
                returnUrl:
                  '/orgs/private-org/projects/private-project/runs/private-run?token=hidden',
              }),
            },
          },
        },
        {
          provide: SessionStore,
          useValue: { signedOut: signedOutState, reauth: () => signedOutState.set(false) },
        },
        { provide: LOCATION_NAVIGATE, useValue: navigate },
        { provide: LOGIN_EXTENSION_LOADER, useValue: () => Promise.resolve(null) },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(LoginPage);
    const login = await TestbedHarnessEnvironment.harnessForFixture(fixture, LoginNoticeHarness);
    return { login, navigate };
  }

  it.each([
    ['expired', 'Your session expired. Sign in again to continue.'],
    ['signed-out', 'Signed out on this tab'],
    ['failed', 'Sign-in failed. Choose a provider to try again.'],
    ['unavailable', 'Sign-in is temporarily unavailable. Choose a provider to try again.'],
  ])(
    'renders the recognized %s reason while keeping both provider actions available',
    async (reason, title) => {
      const { login } = await open(reason);
      expect(await (await login.notice()).text()).toContain(title);
      expect(await (await login.notice()).getAttribute('aria-live')).toBe('polite');
      const providers = await login.providers();
      expect(providers).toHaveLength(2);
      for (const provider of providers) expect(await provider.getProperty('disabled')).toBe(false);
      expect(await (await login.destination()).text()).not.toMatch(
        /private-org|private-project|private-run|hidden/,
      );
    },
  );

  it.each([null, '', 'EXPIRED', '<script>alert(1)</script>'])(
    'does not echo or invent a notice for the unrecognized reason %s',
    async (reason) => {
      const { login } = await open(reason);
      expect(await (await login.notice()).text()).toBe('');
      expect(await (await login.notice()).hasClass('sr-only')).toBe(true);
    },
  );

  it('uses the signed-out latch as fallback without claiming server sign-out', async () => {
    const { login } = await open('not-a-reason', true);
    expect(await (await login.notice()).text()).toContain('Signed out on this tab');
    expect(await (await login.notice()).text()).toContain('Server sign-out is not available yet.');
  });

  it('gives a recognized URL reason precedence over the signed-out latch', async () => {
    const { login } = await open('expired', true);
    expect(await (await login.notice()).text()).toBe(
      'Your session expired. Sign in again to continue.',
    );
    expect(await (await login.notice()).text()).not.toContain('Signed out');
  });

  it('replaces an error with a single busy notice and starts only one provider redirect', async () => {
    const { login, navigate } = await open('failed');
    expect(await (await login.notice()).text()).toContain('Sign-in failed.');
    const primary = await login.primary();
    await primary.click();
    await primary.click();
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(await (await login.notice()).text()).toBe('Opening your sign-in provider…');
    for (const provider of await login.providers()) {
      expect(await provider.getProperty('disabled')).toBe(true);
    }
  });
});
