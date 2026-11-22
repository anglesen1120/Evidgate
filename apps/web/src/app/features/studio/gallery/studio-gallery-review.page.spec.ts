import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { StudioGalleryReviewPage } from './studio-gallery-review.page';
import { DraftHarness, GalleryHarness } from './studio-gallery.testing';

provideLayoutTestStubs();

async function render(state?: string, desktop = true) {
  const route = new BehaviorSubject(convertToParamMap(state ? { state } : {}));
  TestBed.configureTestingModule({
    providers: [
      { provide: ActivatedRoute, useValue: { queryParamMap: route } },
      {
        provide: NarrowMutationPolicy,
        useValue: { narrow: signal(!desktop), canMutate: signal(desktop) },
      },
    ],
  });
  const fixture = TestBed.createComponent(StudioGalleryReviewPage);
  await fixture.whenStable();
  return {
    page: await TestbedHarnessEnvironment.harnessForFixture(fixture, GalleryHarness),
    overlay: TestbedHarnessEnvironment.documentRootLoader(fixture),
    route,
  };
}

describe('StudioGalleryReviewPage', () => {
  afterEach(() => TestBed.resetTestingModule());

  it.each([
    ['default', 'Required assertions', 3],
    ['unknown', 'Required assertions', 3],
    ['loading', 'Loading the supplied fixture', 0],
    ['empty', 'No templates are available', 0],
    ['no-match', 'No results match', 0],
    ['nomatch', 'No results match', 0],
    ['error', 'req-114', 0],
    ['401', 'Session expired', 0],
    ['404', 'Not found', 0],
    ['403', 'Available details are read-only', 3],
    ['429', 'Retry-After: 30 s', 3],
    ['412', 'not a create response', 3],
    ['success', 'd-114 / dr-4', 3],
    ['record-unavailable', 'Studio record unavailable', 0],
  ] as const)('renders %s without inferring any verdict', async (state, expected, count) => {
    const { page } = await render(state);
    const text = await (await page.host()).text();
    expect(text).toContain(expected);
    expect(text).not.toMatch(/\bPASS\b/);
    expect(await page.cards()).toHaveLength(count);
    if (['403', '412', '429', 'success'].includes(state)) {
      expect(await (await page.create()).getProperty('disabled')).toBe(true);
    }
  });

  it.each([
    ['invalid', 'Enter a required assertion'],
    ['busy', 'Duplicate submission is disabled'],
    ['createerror', 'Form retained'],
  ] as const)('opens the %s draft example', async (state, expected) => {
    const { overlay } = await render(state);
    const dialog = await overlay.getHarness(DraftHarness);
    expect(await (await dialog.host()).text()).toContain(expected);
    expect(await (await dialog.submit()).getProperty('disabled')).toBe(state !== 'createerror');
  });

  it('filters the cards and the selected preview together, and can clear a miss', async () => {
    const { page } = await render();
    const search = await page.search();
    await search.setInputValue(' rounding ');
    await search.dispatchEvent('input');
    expect(await page.cards()).toHaveLength(1);
    expect(await (await page.preview()).text()).toContain('Sample line items');
    await search.setInputValue('no such template');
    await search.dispatchEvent('input');
    expect(await page.cards()).toHaveLength(0);
    await (await page.recover()).click();
    expect(await page.cards()).toHaveLength(3);
  });

  it('previews the chosen API card without assigning an invented draft', async () => {
    const { page, overlay } = await render();
    const [, api] = await page.pick();
    if (!api) throw new Error('Expected the API template');
    await api.click();
    expect(await (await page.preview()).text()).toContain('Unauthorized principal');
    await (await page.create()).click();
    const dialog = await overlay.getHarness(DraftHarness);
    await (await dialog.submit()).click();
    expect(await (await dialog.host()).text()).toContain('No draft ID has been assigned');
  });

  it('replaces a populated page on a route change, without stale cards', async () => {
    const { page, route } = await render();
    route.next(convertToParamMap({ state: '404' }));
    expect(await page.cards()).toHaveLength(0);
    expect(await (await page.host()).text()).not.toContain('Browser checkout confirmation');
  });

  it('keeps details readable and explains the desktop restriction on narrow screens', async () => {
    const { page } = await render('busy', false);
    expect(await (await page.host()).text()).toContain('Desktop required');
    expect(await page.cards()).toHaveLength(3);
  });
});
