import { TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { galleryFixture } from './studio-gallery.fixture';
import { StudioGalleryReviewPage } from './studio-gallery-review.page';
import { DraftHarness, GalleryHarness, ViewHarness } from './studio-gallery.testing';

provideLayoutTestStubs();

async function render(state?: string, desktop = true) {
  const route = new BehaviorSubject(convertToParamMap(state ? { state } : {}));
  const narrow = signal(!desktop);
  TestBed.configureTestingModule({
    providers: [
      { provide: ActivatedRoute, useValue: { queryParamMap: route } },
      {
        provide: NarrowMutationPolicy,
        useValue: { narrow, canMutate: signal(desktop) },
      },
    ],
  });
  const fixture = TestBed.createComponent(StudioGalleryReviewPage);
  await fixture.whenStable();
  return {
    fixture,
    page: await TestbedHarnessEnvironment.harnessForFixture(fixture, GalleryHarness),
    overlay: TestbedHarnessEnvironment.documentRootLoader(fixture),
    narrow,
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
    expect(await page.rows()).toHaveLength(count);
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

  it('counts the loaded source groups and announces how many filtered rows remain', async () => {
    const { page } = await render();
    const team = await page.source('team');
    expect(await (await page.source('built')).text()).toMatch(/Built-in\s*2/);
    expect(await team.text()).toMatch(/Team\s*1/);
    await team.click();
    expect(await team.getAttribute('aria-pressed')).toBe('true');
    expect(await page.rows()).toHaveLength(1);
    expect(await (await page.host()).text()).toContain('Showing 1 of 3 templates');
  });

  it('filters the rows and their detail together, and can clear a miss', async () => {
    const { page, overlay } = await render();
    const search = await page.search();
    await search.setInputValue(' rounding ');
    await search.dispatchEvent('input');
    expect(await page.rows()).toHaveLength(1);
    const [unit] = await page.pick();
    if (!unit) throw new Error('Expected the unit template');
    await unit.click();
    const view = await overlay.getHarness(ViewHarness);
    expect(await (await view.host()).text()).toContain('Sample line items');
    await (await view.close()).click();
    await search.setInputValue('no such template');
    await search.dispatchEvent('input');
    expect(await page.rows()).toHaveLength(0);
    await (await page.recover()).click();
    expect(await page.rows()).toHaveLength(3);
  });

  it('uses the selected API template for draft preview without inventing a draft', async () => {
    const { page, overlay } = await render();
    const [, api] = await page.pick();
    if (!api) throw new Error('Expected the API template');
    await api.click();
    const view = await overlay.getHarness(ViewHarness);
    expect(await (await view.host()).text()).toContain('Unauthorized principal');
    await (await view.close()).click();
    await (await page.create()).click();
    const dialog = await overlay.getHarness(DraftHarness);
    await (await dialog.submit()).click();
    expect(await (await dialog.host()).text()).toContain('No draft ID has been assigned');
  });

  it('opens one labelled View from the table, focuses its title and returns focus on Escape', async () => {
    const { page, overlay } = await render();
    expect(await (await page.table()).getAttribute('aria-labelledby')).toBeTruthy();
    expect(await Promise.all((await page.headers()).map((cell) => cell.text()))).toEqual([
      'Template',
      'Engine / variant',
      'Template source',
      'Required assertions',
      'Actions',
    ]);
    const [, api] = await page.pick();
    if (!api) throw new Error('Expected the API template');
    await api.focus();
    await api.click();
    const [view] = await overlay.getAllHarnesses(ViewHarness);
    if (!view) throw new Error('Expected a template dialog');
    expect(await overlay.getAllHarnesses(ViewHarness)).toHaveLength(1);
    const title = await view.title();
    expect(await title.text()).toBe('API order authorization');
    expect(await (await view.host()).getAttribute('aria-labelledby')).toBe(
      await title.getAttribute('id'),
    );
    expect(await title.isFocused()).toBe(true);
    const facts = await Promise.all((await view.terms()).map((term) => term.text()));
    expect(facts).toEqual(
      expect.arrayContaining([
        'Precondition',
        'Action',
        'Required assertions',
        'Cleanup',
        'Test intent',
      ]),
    );
    await title.sendKeys(TestKey.ESCAPE);
    expect(await overlay.hasHarness(ViewHarness)).toBe(false);
    expect(await api.isFocused()).toBe(true);
  });

  it('escapes invisible format characters and isolates each template fact', async () => {
    const { fixture, page, overlay } = await render();
    const item = galleryFixture.templates[0];
    if (!item) throw new Error('Missing checkout template');
    const values = {
      intent: 'Intent\u202Evalue',
      precondition: 'Before\u2066value',
      action: 'Action\u200Dvalue',
      assertion: 'Assert\u202Cvalue',
      engine: 'Engine\u200Bvalue',
      variant: 'Variant\u2069value',
    };
    fixture.componentRef.setInput('viewModel', {
      ...galleryFixture,
      templates: [{ ...item, ...values }],
    });
    const [trigger] = await page.pick();
    if (!trigger) throw new Error('Missing template View');
    await trigger.click();
    const view = await overlay.getHarness(ViewHarness);
    expect(await Promise.all((await view.isolatedFacts()).map((fact) => fact.text()))).toEqual([
      'Engine⟨U+200B⟩value',
      'Variant⟨U+2069⟩value',
      'Intent⟨U+202E⟩value',
      'Before⟨U+2066⟩value',
      'Action⟨U+200D⟩value',
      'Assert⟨U+202C⟩value',
    ]);
  });

  it('replaces a populated page on a route change without stale rows or details', async () => {
    const { page, route, overlay } = await render();
    const [checkout] = await page.pick();
    if (!checkout) throw new Error('Expected checkout');
    await checkout.click();
    route.next(convertToParamMap({ state: '404' }));
    expect(await page.rows()).toHaveLength(0);
    expect(await overlay.hasHarness(ViewHarness)).toBe(false);
    expect(await (await page.host()).text()).not.toContain('Browser checkout confirmation');
  });

  it('keeps edited preview input on resize but blocks the preview action', async () => {
    const { page, overlay, narrow } = await render();
    await (await page.create()).click();
    const dialog = await overlay.getHarness(DraftHarness);
    const assertion = await dialog.assertion();
    await assertion.setInputValue('Unsaved local expectation');
    await assertion.dispatchEvent('input');
    narrow.set(true);
    expect(await (await dialog.assertion()).getProperty('readOnly')).toBe(true);
    expect(await (await dialog.submit()).getProperty('disabled')).toBe(true);
    expect(await (await dialog.assertion()).getProperty('value')).toBe('Unsaved local expectation');
    await (await dialog.cancel()).click();
    expect(await (await dialog.keep()).isFocused()).toBe(true);
  });

  it('keeps details readable and explains the desktop restriction on narrow screens', async () => {
    const { page } = await render('busy', false);
    expect(await (await page.host()).text()).toContain('Desktop required');
    expect(await page.rows()).toHaveLength(3);
  });
});
