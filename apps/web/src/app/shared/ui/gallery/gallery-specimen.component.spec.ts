import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { GallerySpecimenComponent } from './gallery-specimen.component';

class SpecimenHarness extends ComponentHarness {
  static hostSelector = 'app-gallery-specimen';
  readonly disclosure = this.locatorFor('button[aria-controls]');
  readonly examples = this.locatorForAll('app-gallery-example');
  readonly value = (state: string) => this.locatorFor(`[data-state="${state}"] [hlmCardContent]`)();
}

describe('GallerySpecimenComponent', () => {
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
