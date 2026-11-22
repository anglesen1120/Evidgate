import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { LOCALE_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { SettingsPageCountsComponent } from './settings-page-counts.component';

class PageCountsHarness extends ComponentHarness {
  static hostSelector = 'app-settings-page-counts';
  readonly terms = this.locatorForAll('dt');
  readonly definitions = this.locatorForAll('dd');
}

describe('SettingsPageCountsComponent', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('qualifies each count individually, formats its number for the locale and preserves zero', async () => {
    TestBed.configureTestingModule({
      imports: [SettingsPageCountsComponent],
      providers: [{ provide: LOCALE_ID, useValue: 'vi' }],
    });
    const fixture = TestBed.createComponent(SettingsPageCountsComponent);
    fixture.componentRef.setInput('metrics', [
      { label: 'Active', icon: 'key', value: 1234 },
      { label: 'Expired', icon: 'scroll', value: 0 },
    ]);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, PageCountsHarness);
    const terms = await harness.terms();
    expect(await Promise.all(terms.map((term) => term.text()))).toEqual(['Active', 'Expired']);
    const [active, expired] = await harness.definitions();
    expect(await active!.text()).toMatch(/1\.234\s*On this page/u);
    expect(await expired!.text()).toMatch(/0\s*On this page/u);
  });

  it('shows the supplied hint for a waiting count without changing other qualifiers', async () => {
    const fixture = TestBed.createComponent(SettingsPageCountsComponent);
    fixture.componentRef.setInput('metrics', [
      {
        label: 'Pending invitations',
        icon: 'clock',
        value: 2,
        hint: 'Awaiting acceptance on this page',
      },
      { label: 'Active', value: 4 },
    ]);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, PageCountsHarness);
    const definitions = await Promise.all((await harness.definitions()).map((item) => item.text()));
    expect(definitions).toEqual([
      expect.stringMatching(/2\s*Awaiting acceptance on this page/u),
      expect.stringMatching(/4\s*On this page/u),
    ]);
  });
});
