import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { ProducerStatusTableHarness } from './producers/producer-status-table.harness';
import { RunDetailPage } from './run-detail.page';
import {
  configureRunDetailTestBed,
  get,
  open,
  run,
  selectTab,
  settle,
} from './run-detail.page.test-support';

describe('RunDetailPage view ownership', () => {
  provideLayoutTestStubs();
  beforeEach(() => configureRunDetailTestBed());
  afterEach(() => TestBed.resetTestingModule());

  it.each(['destroy', 'change run'])('closes the active attempt dialog on %s', async (cause) => {
    const { harness, root } = await open();
    root.querySelector<HTMLButtonElement>('app-attempts-table button[appRowAction]')!.click();
    await settle(harness);
    expect(document.querySelector('app-attempt-inspector')).not.toBeNull();
    if (cause === 'destroy') {
      harness.fixture.destroy();
    } else {
      const nextId = '018f0000-0000-7000-8000-000000000abc';
      get.mockResolvedValue({ ...run, run_id: nextId });
      await harness.navigateByUrl(`/projects/project-1/runs/${nextId}`, RunDetailPage);
      await settle(harness);
      expect(harness.routeNativeElement).toBe(root);
    }
    await vi.waitFor(() => expect(document.querySelector('app-attempt-inspector')).toBeNull());
  });

  it('keeps the producer page size and rows through a tab switch, not across runs', async () => {
    const declared = Array.from({ length: 53 }, (_, i) => `shard-${i}`);
    get.mockImplementation((id) =>
      Promise.resolve({ ...run, run_id: id, declared_producers: declared }),
    );
    const { harness } = await open(run.run_id, '', '?tab=evidence');
    const loader = TestbedHarnessEnvironment.loader(harness.fixture);
    const producer = () => loader.getHarness(ProducerStatusTableHarness);
    let table = await producer();
    await table.chooseSize(50);
    await (await table.next()).click();
    expect(await table.rowIds()).toEqual(declared.slice(50));
    await selectTab(harness, 'attempts');
    expect(await loader.getAllHarnesses(ProducerStatusTableHarness)).toHaveLength(0);
    await selectTab(harness, 'evidence');
    table = await producer();
    expect(await (await table.size()).text()).toContain('50');
    expect(await table.rowIds()).toEqual(declared.slice(50));
    await harness.navigateByUrl('/projects/project-1/runs/other?tab=evidence', RunDetailPage);
    await settle(harness);
    table = await producer();
    expect(await (await table.size()).text()).toContain('20');
    expect(await table.rowIds()).toEqual(declared.slice(0, 20));
  });
});
