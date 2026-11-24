import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { runCreatePageProviders } from '../actions/create/create-run-list.test-support';
import { RunDataService } from '../data/run-data.service';
import { FakeRunDataService, rows, run } from './run-list-store.fixtures';
import { RunListPage } from './run-list.page';

class RunFetchHarness extends ComponentHarness {
  static hostSelector = 'app-run-list-page';
  readonly next = this.locatorFor('button[data-page="next"]');
  readonly apply = this.locatorFor('[data-run-apply]');
  readonly ref = this.locatorFor('#run-ref');
  readonly table = this.locatorFor('[role="table"]');
  readonly firstRow = this.locatorFor('[data-row-id]');
  readonly progress = this.locatorForOptional('[data-table-progress]');
}

describe('Run list fetch feedback', () => {
  provideLayoutTestStubs();

  async function render() {
    const data = new FakeRunDataService();
    data.all = rows(2);
    data.pageSizeOverride = 1;
    const paramMap = of(convertToParamMap({ projectId: 'project-1' }));
    const route = { pathFromRoot: [{ paramMap }], queryParamMap: of(convertToParamMap({})) };
    TestBed.configureTestingModule({
      imports: [RunListPage],
      providers: [
        provideRouter([]),
        ...runCreatePageProviders().providers,
        { provide: RunDataService, useValue: data },
        { provide: ActivatedRoute, useValue: route },
      ],
    });
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(RunListPage);
    await fixture.whenStable();
    const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, RunFetchHarness);
    data.manual = true;
    return { data, fixture, page };
  }

  it('locks Next through a pending page request and clears progress on failure without losing rows', async () => {
    const { data, fixture, page } = await render();
    const next = await page.next();
    await next.click();
    await next.click();
    expect(data.calls).toHaveLength(2);
    expect(await next.getAttribute('aria-disabled')).toBe('true');
    expect(await (await page.table()).getAttribute('aria-busy')).toBe('true');
    await vi.waitFor(async () => expect(await page.progress()).not.toBeNull());
    expect(await (await page.firstRow()).getAttribute('data-row-id')).toBe('run-0');
    data.pending[0]?.reject(new Error('Connection lost'));
    await fixture.whenStable();
    expect(await (await page.table()).getAttribute('aria-busy')).toBe('false');
    expect(await page.progress()).toBeNull();
    expect(await (await page.firstRow()).getAttribute('data-row-id')).toBe('run-0');
  });

  it('locks Apply while the backend replaces filtered rows, then reveals the new result', async () => {
    const { data, fixture, page } = await render();
    const ref = await page.ref();
    await ref.setInputValue('main');
    await ref.dispatchEvent('input');
    const apply = await page.apply();
    await apply.click();
    expect(await apply.getAttribute('aria-disabled')).toBe('true');
    expect(await apply.getProperty<boolean>('disabled')).toBe(false);
    await apply.click();
    expect(data.calls.map((call) => call.request.refName)).toEqual([undefined, 'main']);
    expect(await (await page.firstRow()).getAttribute('data-row-id')).toBe('run-0');
    await vi.waitFor(async () => expect(await page.progress()).not.toBeNull());
    data.pending[0]?.resolve({ items: [run(2)], next_cursor: null });
    await fixture.whenStable();
    expect(await page.progress()).toBeNull();
    expect(await (await page.table()).getAttribute('aria-busy')).toBe('false');
    expect(await apply.getAttribute('aria-disabled')).toBe('false');
    expect(await (await page.firstRow()).getAttribute('data-row-id')).toBe('run-2');
  });
});
