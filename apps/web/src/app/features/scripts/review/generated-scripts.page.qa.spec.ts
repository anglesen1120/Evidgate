import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { generatedScriptsFixture } from './generated-scripts.fixture';
import { GeneratedScriptsPage } from './generated-scripts.page';

class ScriptBoundaryHarness extends ComponentHarness {
  static hostSelector = 'app-generated-scripts-review';
  readonly views = this.locatorForAll('tbody button[aria-haspopup="dialog"]');
  readonly heading = this.locatorFor('h2[tabindex="-1"]');
  readonly statusGroup = this.locatorFor('[role="group"][aria-labelledby="script-status-heading"]');
  readonly filters = this.locatorForAll('[role="group"] button[aria-pressed]');
  readonly tabs = this.locatorForAll('[role="tablist"], [aria-orientation="vertical"]');
  readonly dialogs = this.documentRootLocatorFactory().locatorForAll('[role="dialog"]');
}

provideLayoutTestStubs();

async function mount() {
  const params = new BehaviorSubject(convertToParamMap({}));
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { queryParamMap: params } },
    ],
  });
  const fixture = TestBed.createComponent(GeneratedScriptsPage);
  await fixture.whenStable();
  return {
    fixture,
    page: await TestbedHarnessEnvironment.harnessForFixture(fixture, ScriptBoundaryHarness),
  };
}

describe('Generated script empty and localized boundaries', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('returns to the named inventory heading when the last loaded script disappears', async () => {
    const { fixture, page } = await mount();
    const [trigger] = await page.views();
    if (!trigger) throw new Error('Missing script');
    await trigger.click();
    expect(await page.dialogs()).toHaveLength(1);
    fixture.componentRef.setInput('viewModel', { ...generatedScriptsFixture, scripts: [] });
    expect(await page.dialogs()).toHaveLength(0);
    expect(await (await page.heading()).isFocused()).toBe(true);
    expect(await page.views()).toHaveLength(0);
  });

  it('keeps expanded Vietnamese status labels whole in a nonwrapping local filter strip', async () => {
    const { fixture, page } = await mount();
    const label = 'Chưa có kết quả kiểm chứng đầy đủ cho bản dựng hiện tại';
    fixture.componentRef.setInput('viewModel', {
      ...generatedScriptsFixture,
      scripts: generatedScriptsFixture.scripts.map((row) => ({ ...row, status: label })),
    });
    const strip = await page.statusGroup();
    expect(await strip.hasClass('flex-nowrap')).toBe(true);
    expect(await strip.hasClass('overflow-x-auto')).toBe(true);
    expect(await strip.hasClass('flex-wrap')).toBe(false);
    const [all, status] = await page.filters();
    if (!all || !status) throw new Error('Missing status filters');
    expect(await status.text()).toContain(label);
    expect(await status.hasClass('whitespace-nowrap')).toBe(true);
    expect(await status.hasClass('shrink-0')).toBe(true);
    await status.click();
    expect(await page.views()).toHaveLength(3);
    expect(await page.tabs()).toHaveLength(0);
  });
});
