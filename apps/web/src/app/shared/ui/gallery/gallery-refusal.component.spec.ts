import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { provideGalleryLayout } from './gallery-layout.testing';
import { GalleryRefusalComponent } from './gallery-refusal.component';

class RefusalHarness extends ComponentHarness {
  static hostSelector = 'app-gallery-refusal';
  readonly opener = this.locatorFor('button[hlmDialogTrigger]');
  readonly outcome = this.locatorFor('[role="status"]');
}

class RefusalDialogHarness extends ComponentHarness {
  static hostSelector = '[role="alertdialog"]';
  readonly firstReason = this.locatorFor('input[type="radio"]');
  readonly note = this.locatorFor('textarea');
  readonly decline = this.locatorFor('[data-decline]');
  readonly statusIcon = this.locatorFor('header > [hlmDialogStatusIcon]');
  readonly subject = this.locatorFor('header > [hlmDialogSubject]');
}

describe('Refusal specimen', () => {
  provideGalleryLayout();

  it('requires a supplied reason and reports a local preview, not a submitted decision', async () => {
    const fixture = TestBed.createComponent(GalleryRefusalComponent);
    fixture.componentRef.setInput('reasons', [
      { code: 'producer_missing', text: 'No producer evidence.' },
    ]);
    const root = TestbedHarnessEnvironment.documentRootLoader(fixture);
    const sample = await TestbedHarnessEnvironment.harnessForFixture(fixture, RefusalHarness);
    const opener = await sample.opener();
    await opener.focus();
    await opener.click();
    const dialog = await root.getHarness(RefusalDialogHarness);
    expect(await (await dialog.statusIcon()).getAttribute('aria-hidden')).toBe('true');
    expect(await (await dialog.subject()).text()).toContain('run-1842');
    expect(await (await dialog.decline()).getProperty('disabled')).toBe(true);
    await (await dialog.firstReason()).click();
    const note = await dialog.note();
    await note.setInputValue('Check the producer upload.');
    await note.dispatchEvent('input');
    await (await dialog.decline()).click();
    expect(await root.hasHarness(RefusalDialogHarness)).toBe(false);
    expect(await (await sample.outcome()).text()).toContain('producer_missing');
    expect(await (await sample.outcome()).text()).toContain('Check the producer upload.');
    expect(await (await sample.outcome()).text()).toContain('Nothing was sent.');
    expect(await opener.isFocused()).toBe(true);
  });
});
