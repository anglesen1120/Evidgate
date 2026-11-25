import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { runCreatePageProviders } from '../actions/create/create-run-list.test-support';
import { RunDataService } from '../data/run-data.service';
import { FakeRunDataService, rows } from './run-list-store.fixtures';
import { RunListPage } from './run-list.page';

class RunMetricsHarness extends ComponentHarness {
  static hostSelector = 'app-run-list-page';
  readonly loaded = this.locatorForOptional('[data-run-metrics] dd > app-number-value');
  readonly filters = this.locatorFor('button[aria-controls="run-filter-panel"]');
  async loadedCount(): Promise<string | null> {
    return (await (await this.loaded())?.text()) ?? null;
  }
  async chooseSize(size: number): Promise<void> {
    const trigger = await this.locatorFor('app-data-table-pager hlm-select-trigger button')();
    await trigger.click();
    await (
      await this.documentRootLocatorFactory().locatorFor(`[role="option"][data-value="${size}"]`)()
    ).click();
  }
}

describe('Run list metric strip', () => {
  provideLayoutTestStubs();

  async function render() {
    const data = new FakeRunDataService();
    data.all = rows(21);
    const project = new BehaviorSubject(convertToParamMap({ projectId: 'project-1' }));
    const query = new BehaviorSubject(convertToParamMap({}));
    TestBed.configureTestingModule({
      imports: [RunListPage],
      providers: [
        provideRouter([]),
        ...runCreatePageProviders().providers,
        { provide: RunDataService, useValue: data },
        {
          provide: ActivatedRoute,
          useValue: { pathFromRoot: [{ paramMap: project }], queryParamMap: query },
        },
      ],
    });
    const fixture = TestBed.createComponent(RunListPage);
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await fixture.whenStable();
    const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, RunMetricsHarness);
    data.manual = true;
    return { data, fixture, project, query, page };
  }

  it('retains the loaded count until a new page size arrives', async () => {
    const { data, fixture, page } = await render();
    expect(await page.loadedCount()).toBe('20');

    await page.chooseSize(50);
    expect(data.calls.map(({ request }) => request.limit)).toEqual([20, 50]);
    expect(await page.loadedCount()).toBe('20');

    data.pending[0]?.resolve({ items: data.all, next_cursor: null });
    await fixture.whenStable();
    expect(await page.loadedCount()).toBe('21');
  });

  it('keeps filters on their disclosure and never reuses a previous project count', async () => {
    const { data, fixture, project, query, page } = await render();
    expect(await (await page.filters()).text()).toBe('Filters (0)');
    query.next(convertToParamMap({ state: 'running' }));
    expect(await page.loadedCount()).toBe('20');
    data.pending[0]?.resolve({ items: [], next_cursor: null });
    await fixture.whenStable();
    expect(await page.loadedCount()).toBe('0');
    expect(await (await page.filters()).text()).toBe('Filters (1)');

    project.next(convertToParamMap({ projectId: 'project-2' }));
    expect(await page.loadedCount()).toBeNull();
    data.pending[1]?.resolve({ items: data.all, next_cursor: null });
    await fixture.whenStable();
    expect(await page.loadedCount()).toBe('21');
    expect(await (await page.filters()).text()).toBe('Filters (1)');
  });
});
