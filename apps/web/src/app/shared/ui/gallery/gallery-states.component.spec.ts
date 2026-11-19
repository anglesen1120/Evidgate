import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { registerLocaleData } from '@angular/common';
import localeVi from '@angular/common/locales/vi';
import { LOCALE_ID, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { clearTranslations, loadTranslations } from '@angular/localize';
import { Xliff1TranslationParser } from '@angular/localize/tools';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { GalleryStatesComponent } from './gallery-states.component';

class LiveStatusSpecimenHarness extends ComponentHarness {
  static hostSelector = 'app-gallery-states';
  readonly region = this.locatorFor('[data-demo="live-status"] app-live-status [role="status"]');
  readonly regions = this.locatorForAll(
    '[data-demo="live-status"] [role="status"], [data-demo="live-status"] [aria-live]',
  );
  readonly preview = this.locatorFor('[data-demo="live-status-preview"]');
  readonly announce = this.locatorFor('[data-demo="live-status-announce"]');
  readonly change = this.locatorFor('[data-demo="live-status-change"]');
  readonly clear = this.locatorFor('[data-demo="live-status-clear"]');
}

function vietnameseMessages(): Record<string, string> {
  const file = resolve('src/locale/messages.vi.xlf');
  const contents = readFileSync(file, 'utf8');
  const parser = new Xliff1TranslationParser();
  const analysis = parser.analyze(file, contents);
  if (!analysis.canParse) throw new Error('Expected a Vietnamese XLIFF catalogue');
  const bundle = parser.parse(file, contents, analysis.hint);
  const translations: Record<string, { text: string }> = bundle.translations;
  return Object.fromEntries(Object.entries(translations).map(([id, t]) => [id, t.text]));
}

registerLocaleData(localeVi);

const messages = {
  en: {
    first: 'Showing the loaded cases. Refine the filters to find a specific case.',
    changed: 'The filters changed. Showing the matching loaded cases.',
    empty: 'No announcement',
  },
  vi: {
    first: 'Đang hiển thị các ca đã tải. Thu hẹp bộ lọc để tìm một ca cụ thể.',
    changed: 'Bộ lọc đã thay đổi. Đang hiển thị các ca đã tải phù hợp.',
    empty: 'Không có thông báo',
  },
};

const cases = [
  { locale: 'en', theme: 'light' },
  { locale: 'en', theme: 'dark' },
  { locale: 'vi', theme: 'light' },
  { locale: 'vi', theme: 'dark' },
] as const;

describe('GalleryStatesComponent live status specimen', () => {
  let previousTheme: string | null;
  let previousLang: string | null;

  beforeEach(() => {
    previousTheme = document.documentElement.getAttribute('data-theme');
    previousLang = document.documentElement.getAttribute('lang');
    clearTranslations();
  });

  afterEach(() => {
    clearTranslations();
    TestBed.resetTestingModule();
    if (previousTheme === null) document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', previousTheme);
    if (previousLang === null) document.documentElement.removeAttribute('lang');
    else document.documentElement.setAttribute('lang', previousLang);
  });

  it.each(cases.flatMap((variant) => [360, 1280].map((width) => ({ ...variant, width }))))(
    'keeps one polite region through empty, announced, changed and cleared text: $locale / $theme / $width px',
    async ({ locale, theme, width }) => {
      document.documentElement.setAttribute('data-theme', theme);
      document.documentElement.setAttribute('lang', locale);
      if (locale === 'vi') loadTranslations(vietnameseMessages());
      await TestBed.configureTestingModule({
        imports: [GalleryStatesComponent],
        providers: [provideZonelessChangeDetection(), { provide: LOCALE_ID, useValue: locale }],
      }).compileComponents();
      const fixture = TestBed.createComponent(GalleryStatesComponent);
      const root = fixture.nativeElement as HTMLElement;
      root.style.width = `${width}px`;
      const harness = await TestbedHarnessEnvironment.harnessForFixture(
        fixture,
        LiveStatusSpecimenHarness,
      );
      const region = await harness.region();
      const preview = await harness.preview();
      const expected = messages[locale];
      expect((await region.text()).trim()).toBe('');
      expect(await region.getAttribute('aria-live')).toBe('polite');
      expect(await region.getAttribute('aria-atomic')).toBe('true');
      expect(await preview.text()).toBe(expected.empty);

      const announce = await harness.announce();
      await announce.focus();
      await announce.click();
      expect((await region.text()).trim()).toBe(expected.first);
      expect(await preview.text()).toBe(expected.first);
      expect(await announce.isFocused()).toBe(true);

      const change = await harness.change();
      await change.focus();
      await change.click();
      expect((await region.text()).trim()).toBe(expected.changed);
      expect(await preview.text()).toBe(expected.changed);
      expect(await change.isFocused()).toBe(true);

      const clear = await harness.clear();
      await clear.focus();
      await clear.click();
      expect((await region.text()).trim()).toBe('');
      expect(await preview.text()).toBe(expected.empty);
      expect(await clear.isFocused()).toBe(true);
      expect(await harness.regions()).toHaveLength(1);
    },
  );
});
