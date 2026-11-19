import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { LiveStatusComponent } from './live-status.component';

class LiveStatusHarness extends ComponentHarness {
  static hostSelector = 'app-live-status';
  readonly regions = this.locatorForAll('[role="status"]');
  readonly region = this.locatorFor('[role="status"]');
}

describe('LiveStatusComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [LiveStatusComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());

  it('keeps one mounted polite region and replaces its text in place', async () => {
    const fixture = TestBed.createComponent(LiveStatusComponent);
    fixture.detectChanges();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, LiveStatusHarness);
    expect((await (await harness.region()).text()).trim()).toBe('');
    expect(await (await harness.region()).getAttribute('aria-live')).toBe('polite');

    fixture.componentRef.setInput('text', 'Case list is at its load limit');
    fixture.detectChanges();
    expect((await (await harness.region()).text()).trim()).toBe('Case list is at its load limit');
    expect(await harness.regions()).toHaveLength(1);

    fixture.componentRef.setInput('text', '');
    fixture.detectChanges();
    expect((await (await harness.region()).text()).trim()).toBe('');
    expect(await harness.regions()).toHaveLength(1);
  });
});
