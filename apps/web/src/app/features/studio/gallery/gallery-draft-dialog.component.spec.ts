import { TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { galleryFixture } from './studio-gallery.fixture';
import { GalleryDraftDialogComponent } from './gallery-draft-dialog.component';
import { BackdropHarness, DraftHarness, TemplateOptionHarness } from './studio-gallery.testing';

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

  it('guards replacement of edited fields and switches only after discard', async () => {
    const { fixture, dialog } = await render('preview');
    const root = TestbedHarnessEnvironment.documentRootLoader(fixture);
    const title = await dialog.title();
    expect(await title.isFocused()).toBe(true);
    expect(await (await dialog.host()).getAttribute('aria-labelledby')).toBe(
      await title.getAttribute('id'),
    );
    const assertion = await dialog.assertion();
    await assertion.setInputValue('Changed assertion');
    await assertion.dispatchEvent('input');
    const replace = async () => {
      await (await dialog.template()).click();
      const [, api] = await root.getAllHarnesses(TemplateOptionHarness);
      if (!api) throw new Error('Missing API template option');
      await (await api.host()).click();
    };
    await replace();
    await (await dialog.keep()).sendKeys(TestKey.ESCAPE);
    expect(await (await dialog.assertion()).getProperty('value')).toBe('Changed assertion');
    await replace();
    await (await dialog.discard()).click();
    expect(await (await dialog.assertion()).getProperty('value')).toBe(
      galleryFixture.templates[1]?.assertion,
    );
  });

  it('does not guard dismissal after an edit is reverted', async () => {
    const { fixture, dialog } = await render('preview');
    const assertion = await dialog.assertion();
    const initial = await assertion.getProperty<string>('value');
    for (const value of ['A different assertion', initial]) {
      await assertion.setInputValue(value);
      await assertion.dispatchEvent('input');
    }
    const unloading = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(unloading);
    expect(unloading.defaultPrevented).toBe(false);
    const dismissed = vi.fn();
    fixture.componentInstance.dismissed.subscribe(dismissed);
    await (await dialog.cancel()).click();
    expect(dismissed).toHaveBeenCalledOnce();
  });

  it.each(['escape', 'header close', 'backdrop'] as const)(
    'guards %s, keeps the field and discards only on confirmation',
    async (exit) => {
      const { dialog, fixture } = await render('preview');
      const dismissed = vi.fn();
      const historical = vi.fn();
      fixture.componentInstance.dismissed.subscribe(dismissed);
      fixture.componentInstance.historical.subscribe(historical);
      const assertion = await dialog.assertion();
      await assertion.focus();
      await assertion.setInputValue('Check a different receipt');
      await assertion.dispatchEvent('input');
      const unloading = new Event('beforeunload', { cancelable: true });
      window.dispatchEvent(unloading);
      expect(unloading.defaultPrevented).toBe(true);
      if (exit === 'escape') await assertion.sendKeys(TestKey.ESCAPE);
      else if (exit === 'header close') await (await dialog.close()).click();
      else {
        const loader = TestbedHarnessEnvironment.documentRootLoader(fixture);
        const backdrop = await loader.getHarness(BackdropHarness);
        await (await backdrop.host()).click();
      }
      const keep = await dialog.keep();
      expect(await keep.isFocused()).toBe(true);
      expect(dismissed).not.toHaveBeenCalled();
      await keep.click();
      expect(await (await dialog.assertion()).getProperty('value')).toBe(
        'Check a different receipt',
      );
      expect(await (await dialog.assertion()).isFocused()).toBe(true);
      await (await dialog.cancel()).click();
      await (await dialog.discard()).click();
      expect(dismissed).toHaveBeenCalledOnce();
      expect(historical).not.toHaveBeenCalled();
    },
  );

  it('emits the historical example only for the unchanged checkout specimen', async () => {
    const { dialog, fixture } = await render('preview');
    const success = vi.fn();
    fixture.componentInstance.historical.subscribe(success);
    await (await dialog.submit()).click();
    expect(success).toHaveBeenCalledOnce();
  });

  it('allows historical completion after switching from another template to checkout', async () => {
    const { fixture, dialog } = await render('preview');
    fixture.componentRef.setInput('initial', galleryFixture.templates[1]);
    const root = TestbedHarnessEnvironment.documentRootLoader(fixture);
    await (await dialog.template()).click();
    const [checkout] = await root.getAllHarnesses(TemplateOptionHarness);
    if (!checkout) throw new Error('Missing checkout template option');
    await (await checkout.host()).click();
    let guardedDuringCompletion = true;
    const historical = vi.fn(() => {
      const unloading = new Event('beforeunload', { cancelable: true });
      window.dispatchEvent(unloading);
      guardedDuringCompletion = unloading.defaultPrevented;
    });
    fixture.componentInstance.historical.subscribe(historical);
    await (await dialog.submit()).click();
    expect(historical).toHaveBeenCalledOnce();
    expect(guardedDuringCompletion).toBe(false);
    await (await dialog.title()).sendKeys(TestKey.ESCAPE);
    expect(await root.hasHarness(DraftHarness)).toBe(false);
  });

  it('escapes and isolates the read-only variant without changing editable input', async () => {
    const { fixture, dialog } = await render('preview');
    const item = galleryFixture.templates[0];
    if (!item) throw new Error('Missing checkout template');
    fixture.componentRef.setInput('initial', {
      ...item,
      variant: 'chromium\u202Evi',
      assertion: 'Keep\u200Doriginal',
    });
    expect(await (await dialog.variant()).text()).toBe('chromium⟨U+202E⟩vi');
    expect(await (await dialog.assertion()).getProperty('value')).toBe('Keep\u200Doriginal');
  });
});
