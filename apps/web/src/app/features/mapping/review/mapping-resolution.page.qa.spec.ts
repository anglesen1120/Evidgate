import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { mappingResolutionFixture } from './mapping-resolution.fixture';
import { MappingResolutionPage } from './mapping-resolution.page';

class MappingBoundaryHarness extends ComponentHarness {
  static hostSelector = 'app-mapping-resolution-review';
  readonly view = (id: string) => this.locatorFor(`button[data-row="${id}"]`)();
  readonly views = this.locatorForAll('button[data-row]');
  readonly dialog = this.documentRootLocatorFactory().locatorForOptional('[role="dialog"]');
  readonly denied = this.locatorForOptional('app-forbidden-pattern');
  readonly heading = this.locatorFor('h1');
}

provideLayoutTestStubs();

async function mount() {
  const desktop = signal(true);
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      {
        provide: NarrowMutationPolicy,
        useValue: { canMutate: desktop, narrow: () => !desktop(), allowsMutation: desktop },
      },
    ],
  });
  const fixture = TestBed.createComponent(MappingResolutionPage);
  await fixture.whenStable();
  return {
    fixture,
    page: await TestbedHarnessEnvironment.harnessForFixture(fixture, MappingBoundaryHarness),
  };
}

describe('Mapping record lifecycle boundaries', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('closes a removed record and focuses a remaining readable View', async () => {
    const { fixture, page } = await mount();
    const remaining = await page.view('map-62');
    await (await page.view('map-63')).click();
    expect(await (await page.dialog())?.text()).toContain('Checkout confirmation');
    fixture.componentRef.setInput('viewModel', {
      ...mappingResolutionFixture,
      rows: mappingResolutionFixture.rows.filter((row) => row.id !== 'map-63'),
    });
    expect(await page.dialog()).toBeNull();
    expect(await page.views()).toHaveLength(1);
    expect(await remaining.isFocused()).toBe(true);
  });

  it('removes the dialog and its sensitive record when the supplied scope loses read access', async () => {
    const { fixture, page } = await mount();
    await (await page.view('map-63')).click();
    expect(await (await page.dialog())?.text()).toContain('case-checkout-17');
    fixture.componentRef.setInput('viewModel', { ...mappingResolutionFixture, state: 'denied' });
    expect(await page.dialog()).toBeNull();
    expect(await page.denied()).not.toBeNull();
    expect(await page.views()).toHaveLength(0);
    expect(await (await page.host()).text()).not.toContain('case-checkout-17');
  });
});
