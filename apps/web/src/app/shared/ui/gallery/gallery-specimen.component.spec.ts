import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { provideGalleryLayout } from './gallery-layout.testing';
import { GallerySpecimenComponent } from './gallery-specimen.component';

class SpecimenHarness extends ComponentHarness {
  static hostSelector = 'app-gallery-specimen';
  readonly disclosure = this.locatorFor('button[aria-controls^="states-"]');
  readonly filter = this.locatorFor('[data-state="hover"] [role="combobox"]');
  readonly examples = this.locatorForAll('app-gallery-example');
  readonly value = (state: string) => this.locatorFor(`[data-state="${state}"] [hlmCardContent]`)();
}

describe('GallerySpecimenComponent', () => {
  provideGalleryLayout();

  it('dismisses the nested select before the comparison and returns focus on the second Escape', async () => {
    const fixture = TestBed.createComponent(GallerySpecimenComponent);
    fixture.componentRef.setInput('specimen', { id: 'queue', title: 'Review queue' });
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, SpecimenHarness);
    const disclosure = await harness.disclosure();
    await disclosure.click();
    const select = await harness.filter();
    await select.focus();
    await select.click();
    expect(await select.getAttribute('aria-expanded')).toBe('true');

    await select.sendKeys(TestKey.ESCAPE);
    expect(await select.getAttribute('aria-expanded')).toBe('false');
    expect(await disclosure.getAttribute('aria-expanded')).toBe('true');
    expect(await harness.examples()).toHaveLength(9);

    await select.sendKeys(TestKey.ESCAPE);
    expect(await disclosure.getAttribute('aria-expanded')).toBe('false');
    expect(await disclosure.isFocused()).toBe(true);
  });

  it('expands the eight state comparisons without treating missing metrics as zero', async () => {
    const fixture = TestBed.createComponent(GallerySpecimenComponent);
    fixture.componentRef.setInput('specimen', { id: 'kpi', title: 'Shadow pass rate' });
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, SpecimenHarness);
    expect(await harness.examples()).toHaveLength(1);
    await (await harness.disclosure()).click();
    await fixture.whenStable();
    expect(await harness.examples()).toHaveLength(9);
    expect(await (await harness.value('default')).text()).toBe('100%');
    for (const state of ['loading', 'empty', 'error']) {
      expect(await (await harness.value(state)).text()).not.toMatch(/0|%/);
    }
    await (await harness.value('empty')).sendKeys(TestKey.ESCAPE);
    expect(await harness.examples()).toHaveLength(1);
    expect(await (await harness.disclosure()).isFocused()).toBe(true);
  });
});
