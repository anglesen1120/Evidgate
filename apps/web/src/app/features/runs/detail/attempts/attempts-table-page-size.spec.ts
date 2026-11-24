import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { RunsDataService } from '../../../../core/data/runs-data.service';
import { provideApi } from '../../../../core/http/api-client';
import { API_BASE_PATH } from '../../../../core/http/api-origin';
import { JSON_HEADERS, takeRequest } from '../../../../shared/testing/http-data.testing';
import { provideLayoutTestStubs } from '../../../../shared/testing/layout.testing';
import { first, second } from '../run-detail-store.fixtures';
import { configureRunDetailTestBed, open, run } from '../run-detail.page.test-support';

class AttemptsPagerHarness extends ComponentHarness {
  static hostSelector = 'app-attempts-table';
  readonly trigger = this.locatorFor('hlm-select-trigger button');
  readonly previous = this.locatorFor('[data-page="previous"]');
  readonly next = this.locatorFor('[data-page="next"]');
  readonly summary = this.locatorFor('app-data-table-pager [role="status"]');
  readonly alert = this.locatorForOptional('app-error-pattern [role="alert"]');
  readonly retry = this.locatorFor('app-error-pattern button');
  private readonly options = this.documentRootLocatorFactory().locatorForAll('[role="option"]');
  private readonly rows = this.locatorForAll('[data-attempt-row]');

  async chooseSize(size: number) {
    await (await this.trigger()).click();
    for (const option of await this.options()) {
      if ((await option.text()) === String(size)) return option.click();
    }
    throw new Error(`Page size ${size} was not offered`);
  }

  async rowTexts() {
    return Promise.all((await this.rows()).map((row) => row.text()));
  }
}

const base = `${location.origin}${API_BASE_PATH}`;

async function attemptsRequest(limit: number, cursor: string | null = null) {
  const request = await takeRequest(
    TestBed.inject(HttpTestingController),
    `${base}/runs/${run.run_id}/attempts`,
  );
  const query = new URL(request.request.urlWithParams).searchParams;
  expect(query.get('limit')).toBe(String(limit));
  expect(query.get('cursor')).toBe(cursor);
  return request;
}

describe('Run detail attempts page size', () => {
  provideLayoutTestStubs();
  beforeEach(() => {
    configureRunDetailTestBed();
    TestBed.configureTestingModule({
      providers: [provideApi(base), provideHttpClientTesting(), RunsDataService],
    });
    const runs = TestBed.inject(RunsDataService);
    vi.spyOn(runs, 'get').mockResolvedValue(run);
    vi.spyOn(runs, 'listEvidenceRevisions').mockResolvedValue({ items: [], next_cursor: null });
  });
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('keeps the served page and size after a failed resize and commits the retried first page', async () => {
    const opening = open();
    (await attemptsRequest(20)).flush(JSON.stringify(first), { headers: JSON_HEADERS });
    const { harness } = await opening;
    const table = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
      AttemptsPagerHarness,
    );
    const firstRows = await table.rowTexts();
    const paging = (await table.next()).click();
    (await attemptsRequest(20, first.next_cursor)).flush(
      JSON.stringify({ ...second, next_cursor: 'third-page' }),
      { headers: JSON_HEADERS },
    );
    await paging;
    await vi.waitFor(async () => {
      expect(await table.rowTexts()).toHaveLength(second.items.length);
    });
    const secondRows = await table.rowTexts();
    expect(secondRows).toHaveLength(second.items.length);
    expect(secondRows).not.toEqual(firstRows);

    const resizing = table.chooseSize(50);
    (await attemptsRequest(50)).flush(
      JSON.stringify({
        error: {
          code: 'unavailable',
          message: 'Service unavailable',
          request_id: 'request-detail',
          retryable: true,
          details: [],
        },
      }),
      { status: 503, statusText: 'Service Unavailable', headers: JSON_HEADERS },
    );
    await resizing;
    await vi.waitFor(async () => expect(await table.alert()).not.toBeNull());
    expect(await (await table.trigger()).text()).toBe('20');
    expect(await table.rowTexts()).toEqual(secondRows);
    expect(await (await table.summary()).text()).toBe('Page 2');
    expect(await (await table.previous()).getAttribute('aria-disabled')).toBe('false');
    expect(await (await table.next()).getAttribute('aria-disabled')).toBe('false');
    expect(await table.alert()).not.toBeNull();
    const retry = await table.retry();
    expect(await retry.text()).toContain('Retry');

    const retrying = retry.click();
    (await attemptsRequest(50)).flush(JSON.stringify(first), { headers: JSON_HEADERS });
    await retrying;
    await vi.waitFor(async () => expect(await (await table.trigger()).text()).toBe('50'));
    expect(await (await table.trigger()).text()).toBe('50');
    expect(await (await table.summary()).text()).toBe('Page 1');
    expect(await table.rowTexts()).toEqual(firstRows);
    expect(await table.alert()).toBeNull();
  });
});
