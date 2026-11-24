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

class RunSubtitleHarness extends ComponentHarness {
  static hostSelector = 'app-run-list-page';
  readonly description = this.locatorFor('app-page-header .header-description');
  async chooseSize(size: number): Promise<void> {
    const trigger = await this.locatorFor('app-data-table-pager hlm-select-trigger button')();
    await trigger.click();
    await (
      await this.documentRootLocatorFactory().locatorFor(`[role="option"][data-value="${size}"]`)()
    ).click();
  }
}

describe('Run list subtitle', () => {
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
    const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, RunSubtitleHarness);
    data.manual = true;
    return { data, fixture, project, query, page, description: await page.description() };
  }

  it('retains the last ready text until a new page size arrives', async () => {
    const { data, fixture, page, description } = await render();
    const ready = await description.text();
    expect(ready).toBe('Runs on this page: 20 · Active filters: 0');

    await page.chooseSize(50);
    expect(data.calls.map(({ request }) => request.limit)).toEqual([20, 50]);
    expect(await description.text()).toBe(ready);

    data.pending[0]?.resolve({ items: data.all, next_cursor: null });
    await fixture.whenStable();
    expect(await description.text()).toBe('Runs on this page: 21 · Active filters: 0');
  });

  it('retains the committed filter count but never reuses it for another project', async () => {
    const { data, fixture, project, query, description } = await render();
    const ready = await description.text();
    query.next(convertToParamMap({ state: 'running' }));
    expect(await description.text()).toBe(ready);
    data.pending[0]?.resolve({ items: [], next_cursor: null });
    await fixture.whenStable();
    expect(await description.text()).toBe('Runs on this page: 0 · Active filters: 1');

    project.next(convertToParamMap({ projectId: 'project-2' }));
    expect(await description.text()).toContain('Review collection state');
    data.pending[1]?.resolve({ items: data.all, next_cursor: null });
    await fixture.whenStable();
    expect(await description.text()).toBe('Runs on this page: 21 · Active filters: 1');
  });
});
