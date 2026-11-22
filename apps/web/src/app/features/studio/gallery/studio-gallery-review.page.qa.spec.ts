import { ComponentHarness, TestKey } from '@angular/cdk/testing';
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

class HeadingHarness extends ComponentHarness {
  static hostSelector = 'h1';
}

provideLayoutTestStubs();

async function mount() {
  const route = new BehaviorSubject(convertToParamMap({}));
  const narrow = signal(false);
  TestBed.configureTestingModule({
    providers: [
      { provide: ActivatedRoute, useValue: { queryParamMap: route } },
      {
        provide: NarrowMutationPolicy,
        useValue: { narrow, canMutate: () => !narrow(), allowsMutation: () => !narrow() },
      },
    ],
  });
  const fixture = TestBed.createComponent(StudioGalleryReviewPage);
  await fixture.whenStable();
  return {
    fixture,
    route,
    narrow,
    page: await TestbedHarnessEnvironment.harnessForFixture(fixture, GalleryHarness),
    root: TestbedHarnessEnvironment.documentRootLoader(fixture),
  };
}

async function editAssertion(dialog: DraftHarness): Promise<void> {
  const assertion = await dialog.assertion();
  await assertion.focus();
  await assertion.setInputValue('Đơn hàng phải giữ nguyên xác nhận chưa được lưu');
  await assertion.dispatchEvent('input');
}

describe('Studio gallery record lifecycle boundaries', () => {
  afterEach(() => TestBed.resetTestingModule());

  it.each(['Escape', 'Close'] as const)('returns focus to the last row after %s', async (exit) => {
    const { page, root } = await mount();
    const trigger = (await page.pick()).at(-1);
    if (!trigger) throw new Error('Missing last template');
    await trigger.click();
    const view = await root.getHarness(ViewHarness);
    expect(await (await view.title()).text()).toBe('Unit total calculation');
    if (exit === 'Escape') await (await view.title()).sendKeys(TestKey.ESCAPE);
    else await (await view.close()).click();
    expect(await root.hasHarness(ViewHarness)).toBe(false);
    expect(await trigger.isFocused()).toBe(true);
  });

  it('returns to the nearest surviving row when the viewed last template disappears', async () => {
    const { fixture, page, root } = await mount();
    const [, nearest, removed] = await page.pick();
    if (!removed || !nearest) throw new Error('Missing adjacent templates');
    await removed.click();
    expect(await (await root.getHarness(ViewHarness)).host().then((host) => host.text())).toContain(
      'Unit total calculation',
    );
    fixture.componentRef.setInput('viewModel', {
      ...galleryFixture,
      templates: galleryFixture.templates.filter((template) => template.id !== 'unit'),
    });
    expect(await root.hasHarness(ViewHarness)).toBe(false);
    expect(await nearest.isFocused()).toBe(true);
  });

  it('focuses the list heading when the final loaded template disappears', async () => {
    const { fixture, page, root } = await mount();
    const trigger = (await page.pick())[0];
    if (!trigger) throw new Error('Missing template');
    await trigger.click();
    expect(await root.hasHarness(ViewHarness)).toBe(true);
    fixture.componentRef.setInput('viewModel', { ...galleryFixture, templates: [] });
    expect(await root.hasHarness(ViewHarness)).toBe(false);
    const heading = await root.getHarness(HeadingHarness);
    expect(await (await heading.host()).isFocused()).toBe(true);
    expect(await page.rows()).toHaveLength(0);
  });

  it('immediately clears a dirty preview when the project scope changes', async () => {
    const { fixture, page, root } = await mount();
    await (await page.create()).click();
    const dialog = await root.getHarness(DraftHarness);
    await editAssertion(dialog);
    expect(await (await dialog.assertion()).getProperty('value')).toContain('chưa được lưu');
    fixture.componentRef.setInput('viewModel', { ...galleryFixture, scope: 'OTHER · p-other' });
    expect(await root.hasHarness(DraftHarness)).toBe(false);
    expect(await (await page.host()).text()).toContain('OTHER · p-other');
  });

  it('clears dirty preview state on session expiry rather than holding the old dialog open', async () => {
    const { page, root, route } = await mount();
    await (await page.create()).click();
    await editAssertion(await root.getHarness(DraftHarness));
    route.next(convertToParamMap({ state: '401' }));
    expect(await root.hasHarness(DraftHarness)).toBe(false);
    expect(await page.rows()).toHaveLength(0);
    expect(await (await page.host()).text()).not.toContain('chưa được lưu');
  });

  it('keeps a dirty draft through confirmation Escape and discards only explicitly', async () => {
    const { page, root } = await mount();
    const trigger = await page.create();
    await trigger.click();
    const dialog = await root.getHarness(DraftHarness);
    await editAssertion(dialog);
    await (await dialog.cancel()).click();
    const keep = await dialog.keep();
    expect(await keep.isFocused()).toBe(true);
    await keep.sendKeys(TestKey.ESCAPE);
    expect(await (await dialog.assertion()).isFocused()).toBe(true);
    expect(await (await dialog.assertion()).getProperty('value')).toContain('chưa được lưu');
    await (await dialog.close()).click();
    await (await dialog.discard()).click();
    expect(await root.hasHarness(DraftHarness)).toBe(false);
    expect(await trigger.isFocused()).toBe(true);
    expect(await (await page.host()).text()).not.toContain('Historical creation result:');
  });
});
