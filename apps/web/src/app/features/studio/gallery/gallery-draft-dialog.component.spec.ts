import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { galleryFixture } from './studio-gallery.fixture';
import { GalleryDraftDialogComponent } from './gallery-draft-dialog.component';
import { DraftHarness } from './studio-gallery.testing';

provideLayoutTestStubs();

async function render(mode: string) {
  const fixture = TestBed.createComponent(GalleryDraftDialogComponent);
  fixture.componentRef.setInput('templates', galleryFixture.templates);
  fixture.componentRef.setInput('initial', galleryFixture.templates[0]);
  fixture.componentRef.setInput('mode', mode);
  await fixture.whenStable();
  const dialog =
    await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(DraftHarness);
  return { fixture, dialog };
}

describe('GalleryDraftDialogComponent', () => {
  afterEach(() => TestBed.resetTestingModule());

  it.each(['preview', 'busy', 'createerror', 'invalid'])(
    'labels %s as preview only',
    async (mode) => {
      const { dialog } = await render(mode);
      expect(await (await dialog.host()).text()).toContain('Preview only');
      expect(await (await dialog.submit()).getProperty('disabled')).toBe(
        ['busy', 'invalid'].includes(mode),
      );
    },
  );

  it('requires a nonblank assertion and does not assign a historical result to edited input', async () => {
    const { dialog } = await render('invalid');
    const assertion = await dialog.assertion();
    expect(await assertion.getAttribute('aria-invalid')).toBe('true');
    await assertion.setInputValue('   ');
    await assertion.dispatchEvent('input');
    expect(await (await dialog.submit()).getProperty('disabled')).toBe(true);
    await assertion.setInputValue('Order remains in sandbox');
    await assertion.dispatchEvent('input');
    await (await dialog.submit()).click();
    expect(await (await dialog.host()).text()).toContain('No draft ID has been assigned');
  });

  it('emits the historical example only for the unchanged checkout specimen', async () => {
    const { dialog, fixture } = await render('preview');
    const success = vi.fn();
    fixture.componentInstance.historical.subscribe(success);
    await (await dialog.submit()).click();
    expect(success).toHaveBeenCalledOnce();
  });
});
