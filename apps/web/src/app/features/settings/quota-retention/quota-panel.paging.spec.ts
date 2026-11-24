import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';

import { TenantStore } from '../../../core/session/tenant.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { QuotaPanelComponent } from './quota-panel.component';
import { QuotaPanelHarness } from './quota-panel.testing';
import {
  metric,
  mountQuotaStore,
  project,
  serverFailure,
  summary,
} from './quota.store.test-support';

provideLayoutTestStubs();

function page(id: string, next: string | null) {
  return {
    ...summary(metric({ projects: [project({ project_id: id })] })),
    next_cursor: next,
  };
}

describe('QuotaPanelComponent page-size recovery', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('restores the served picker size after a failed resize and retries the chosen size', async () => {
    TestBed.configureTestingModule({ imports: [QuotaPanelComponent] });
    const { store, data, settle } = mountQuotaStore();
    TestBed.inject(TenantStore).setOrg('org-one');
    data.answer = page('first', 'second');
    const fixture = TestBed.createComponent(QuotaPanelComponent);
    await settle();
    data.answer = page('second', 'third');
    store.nextProjectPage();
    await settle();
    const served = store.usageSummary();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, QuotaPanelHarness);
    expect(await (await harness.sizeTrigger()).text()).toBe('20');
    expect(await harness.projectRows()).toHaveLength(1);

    data.failure = serverFailure();
    await (await harness.sizeTrigger()).click();
    const options = await harness.sizeOptions();
    const labels = await Promise.all(options.map((option) => option.text()));
    const chosen = options[labels.indexOf('50')];
    if (!chosen) throw new Error('Missing requested page size');
    await chosen.click();
    await settle();
    expect(data.calls).toHaveLength(3);
    expect(data.calls.at(-1)).toMatchObject({ limit: 50, cursor: undefined });
    expect(await (await harness.sizeTrigger()).text()).toBe('20');
    expect(await (await harness.pageError())?.text()).toContain('previous page is still shown');
    expect(await (await harness.pagers())[0]?.text()).toContain('Page 2');
    expect(await harness.projectRows()).toHaveLength(1);
    expect(store.usageSummary()).toBe(served);
    expect(store.projectPage()).toBe(2);
    expect(store.projectPageSize()).toBe(20);

    data.failure = null;
    data.answer = page('resized', null);
    await (await harness.retryPage()).click();
    await settle();
    expect(data.calls).toHaveLength(4);
    expect(data.calls.at(-1)).toMatchObject({ limit: 50, cursor: undefined });
    expect(await (await harness.sizeTrigger()).text()).toBe('50');
    expect(await harness.pageError()).toBeNull();
    expect(await (await harness.pagers())[0]?.text()).toContain('Page 1');
    expect(store.projectPage()).toBe(1);
    expect(store.projectPageSize()).toBe(50);
    expect(store.usageSummary()?.metrics[0]?.projects[0]?.project_id).toBe('resized');
  });
});
