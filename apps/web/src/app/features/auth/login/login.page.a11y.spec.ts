// The sign-in page is the first core flow a visitor meets, so it carries the gallery's gate: the
// shared WCAG 2.2 AA tag set and exception list, with contrast left to the real-browser run because
// jsdom computes no colours. Both bundles are scanned, language switcher included.
import { registerLocaleData } from '@angular/common';
import localeVi from '@angular/common/locales/vi';
import { LOCALE_ID, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import axe from 'axe-core';
import { vi } from 'vitest';

import exceptionsDocument from '../../../../../e2e/a11y-exceptions.json';
import {
  AXE_JSDOM_RUN_OPTIONS,
  formatViolations,
  unexceptedViolations,
  type A11yException,
} from '../../../../../e2e/a11y/axe-config';
import { AuthFlow, LOCATION_NAVIGATE, type LoginReason } from '../auth-flow';
import { LoginPage } from './login.page';
import { LOGIN_EXTENSION_LOADER } from './login-extension';

registerLocaleData(localeVi);

/** The accepted violations the whole a11y gate shares; this spec honours them too. */
const A11Y_EXCEPTIONS: readonly A11yException[] = exceptionsDocument.exceptions;

const cases = [
  { locale: 'en', theme: 'light' },
  { locale: 'vi', theme: 'light' },
  { locale: 'vi', theme: 'dark' },
] as const;

describe('LoginPage axe acceptance', () => {
  let previousTheme: string | null;
  let previousLang: string | null;

  beforeEach(() => {
    previousTheme = document.documentElement.getAttribute('data-theme');
    previousLang = document.documentElement.getAttribute('lang');
  });

  afterEach(() => {
    if (previousTheme === null) document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', previousTheme);
    if (previousLang === null) document.documentElement.removeAttribute('lang');
    else document.documentElement.setAttribute('lang', previousLang);
    localStorage.removeItem('evidgate-theme');
    vi.restoreAllMocks();
  });

  it.each(cases)(
    'has zero WCAG violations: $locale / $theme',
    async ({ locale, theme }) => {
      if (theme === 'dark') document.documentElement.setAttribute('data-theme', 'dark');
      else document.documentElement.removeAttribute('data-theme');
      document.documentElement.setAttribute('lang', locale);
      // A localized base href is what renders the language switcher, so the scan covers it.
      vi.spyOn(document, 'baseURI', 'get').mockReturnValue(`https://evidgate.test/${locale}/`);
      const flow = {
        returnUrl: signal('/private-target'),
        reason: signal<LoginReason>('none'),
        redirecting: signal(false),
        start: vi.fn(),
      };
      // The page declares `providers: [AuthFlow]`, which shadows a TestBed provider, so the fake
      // flow goes on the component itself and the scan stays about the rendered template.
      TestBed.overrideComponent(LoginPage, {
        set: { providers: [{ provide: AuthFlow, useValue: flow }] },
      });
      await TestBed.configureTestingModule({
        imports: [LoginPage],
        providers: [
          provideZonelessChangeDetection(),
          { provide: LOGIN_EXTENSION_LOADER, useValue: () => Promise.resolve(null) },
          { provide: Router, useValue: { navigated: false } },
          { provide: LOCATION_NAVIGATE, useValue: vi.fn() },
          ...(locale === 'vi' ? [{ provide: LOCALE_ID, useValue: 'vi' }] : []),
        ],
      }).compileComponents();
      const fixture = TestBed.createComponent(LoginPage);
      const root = fixture.nativeElement as HTMLElement;
      document.body.append(root);
      try {
        await fixture.whenStable();
        expect(TestBed.inject(LOCALE_ID)).toBe(locale);
        expect(root.querySelector('#login-heading')?.textContent?.trim()).not.toBe('');
        expect(root.querySelectorAll('.login-providers button')).toHaveLength(2);
        expect(root.querySelector('main')?.getAttribute('aria-labelledby')).toBe('login-heading');
        const result = await axe.run(root, AXE_JSDOM_RUN_OPTIONS);
        // A scan that evaluated no button would pass vacuously, so provenance is asserted too.
        expect(result.passes.some((rule) => rule.id === 'button-name')).toBe(true);
        const found = formatViolations(unexceptedViolations(result.violations, A11Y_EXCEPTIONS));
        expect(found).toEqual([]);
      } finally {
        fixture.destroy();
        root.remove();
      }
    },
    30_000,
  );
});
