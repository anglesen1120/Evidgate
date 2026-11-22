import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';

import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { ReleaseBindingMatrixComponent } from './release-binding-matrix.component';
import { RELEASE_VIEW } from './release-view.fixture';
import type { ReleaseReviewState } from './release-view-state';

class MatrixHarness extends ComponentHarness {
  static hostSelector = 'app-release-binding-matrix';
  row(name: string) {
    return this.locatorFor(`[data-row-id="${name}"]`)();
  }
  readonly rows = this.locatorForAll('[data-row-id]');
  readonly footer = this.locatorFor('footer');
  readonly activeFilter = this.locatorFor('#release-component-filter button[aria-pressed=true]');
  readonly workerIcon = this.locatorFor('[data-row-id="worker"] app-status-pill hlm-icon');
  readonly pending = this.locatorFor('[data-row-id="worker"] [data-tone="pending"]');
  readonly openDigests = this.locatorForAll('details[open]');
}

async function render(state: ReleaseReviewState = 'default', filter = 'all') {
  const fixture = TestBed.createComponent(ReleaseBindingMatrixComponent);
  fixture.componentRef.setInput('rows', RELEASE_VIEW.bindings);
  fixture.componentRef.setInput('state', state);
  fixture.componentRef.setInput('filter', filter);
  fixture.detectChanges();
  return TestbedHarnessEnvironment.harnessForFixture(fixture, MatrixHarness);
}

describe('ReleaseBindingMatrixComponent', () => {
  provideLayoutTestStubs();

  it('preserves each digest and never substitutes a run or a PASS for worker', async () => {
    const matrix = await render();
    const worker = await (await matrix.row('worker')).text();
    expect(worker).toContain('sha256:c44a…2a78');
    expect(worker).toContain('No eligible run');
    expect(worker).not.toMatch(/run-\d|PASS/i);
    expect(await matrix.workerIcon()).toBeDefined();
    expect(await (await matrix.pending()).text()).toContain('Not evaluated');
    expect(await (await matrix.row('web')).text()).toMatch(/run-1844.*ev-4.*Fresh.*PASS/s);
    expect(await (await matrix.row('api')).text()).toMatch(/run-1837.*ev-2.*Predates build.*PASS/s);
  });

  it('does not expose a known digest or verdict for a partially unreadable worker', async () => {
    const matrix = await render('partial');
    const worker = await (await matrix.row('worker')).text();
    expect(worker).toContain('Unavailable');
    expect(worker).not.toMatch(/sha256|Not evaluated|No eligible run/);
    expect(await (await matrix.row('api')).text()).toContain('ev-2');
  });

  it('withholds restricted evidence revisions', async () => {
    const matrix = await render('restricted');
    const web = await (await matrix.row('web')).text();
    expect(web).toContain('Restricted');
    expect(web).not.toContain('ev-4');
  });

  it('exposes all three digest disclosures in the long state', async () => {
    expect(await (await render('long')).openDigests()).toHaveLength(3);
  });

  it('retains the table but shows no matching rows independently of release completeness', async () => {
    expect(await (await render('no-match')).rows()).toHaveLength(0);
  });

  it('narrows the component selection without losing the stale qualification', async () => {
    const matrix = await render('default', 'api');
    expect(await matrix.rows()).toHaveLength(1);
    expect(await (await matrix.footer()).text()).toContain('Showing 1 of 3');
    expect(await (await matrix.activeFilter()).text()).toMatch(/api\s+1/);
    expect(await (await matrix.row('api')).text()).toContain('Predates build');
  });
});
