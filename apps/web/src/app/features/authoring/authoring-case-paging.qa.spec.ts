import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRoute,
  convertToParamMap,
  provideRouter,
  Router,
  type NavigationExtras,
} from '@angular/router';
import type { ManualCaseListItemDto } from '@evidgate/sdk';
import { BehaviorSubject } from 'rxjs';
import { ApiRequestError } from '../../core/http/api-error';
import { apiError } from '../../shared/testing/http-data.testing';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../shared/ui/narrow';
import { AuthoringDataService } from './authoring-data.service';
import { AuthoringCaseTableComponent } from './authoring-case-table.component';

class PagingQaHarness extends ComponentHarness {
  static hostSelector = 'app-authoring-case-table';
  async click(action: 'next' | 'previous' | 'retry') {
    const selector =
      action === 'retry'
        ? 'app-error-pattern button, app-retry-after button'
        : `.pagination > app-data-table-pager [data-page="${action}"]`;
    await (await this.locatorFor(selector)()).click();
  }
  async summary() {
    return (await this.locatorFor('.pagination > app-data-table-pager [role="status"]')()).text();
  }
}
const row = (id: string): ManualCaseListItemDto => ({
  case_id: id,
  code: id,
  node_id: '018f0000-0000-7000-8000-000000000101',
  checklist_item_id: '018f0000-0000-7000-8000-000000000201',
  title: 'Title',
  priority: 'high',
  state: 'draft',
  version: 1,
  linked_live_count: 0,
  linked_case_ids: [],
});

describe('Case pager retry URL ownership', () => {
  provideLayoutTestStubs();
  const query = new BehaviorSubject(convertToParamMap({ q: 'kept-filter', limit: '20' }));
  const list = vi.fn<AuthoringDataService['listManualCases']>();
  const navigate =
    vi.fn<(commands: readonly unknown[], extras?: NavigationExtras) => Promise<boolean>>();
  beforeEach(() => {
    query.next(convertToParamMap({ q: 'kept-filter', limit: '20' }));
    navigate.mockReset().mockResolvedValue(true);
    list.mockReset().mockResolvedValue({ items: [row('first')], next_cursor: 'second' });
    TestBed.configureTestingModule({
      imports: [AuthoringCaseTableComponent],
      providers: [
        provideRouter([]),
        { provide: Router, useValue: { navigate } },
        { provide: AuthoringDataService, useValue: { listManualCases: list } },
        {
          provide: NarrowMutationPolicy,
          useValue: { canMutate: signal(true), narrow: signal(false), allowsMutation: () => true },
        },
        {
          provide: ActivatedRoute,
          useValue: {
            queryParamMap: query,
            pathFromRoot: [
              { paramMap: new BehaviorSubject(convertToParamMap({ projectId: 'project' })) },
            ],
          },
        },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  it('updates the URL only after a failed Next is successfully retried and preserves the filter', async () => {
    const fixture = TestBed.createComponent(AuthoringCaseTableComponent);
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, PagingQaHarness);
    list.mockRejectedValueOnce(
      new ApiRequestError({ ...apiError('network', 0).error, retryable: true }),
    );
    await harness.click('next');
    expect(navigate).not.toHaveBeenCalled();
    expect(await harness.summary()).toContain('1');
    list.mockResolvedValueOnce({ items: [row('middle')], next_cursor: null });
    await harness.click('retry');
    expect(list.mock.lastCall?.[1]).toMatchObject({
      cursor: 'second',
      limit: 20,
      q: 'kept-filter',
    });
    expect(navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParamsHandling: 'merge',
        queryParams: { cursor: 'second' },
        replaceUrl: true,
      }),
    );
    query.next(convertToParamMap({ cursor: 'second', q: 'kept-filter', limit: '20' }));
    await fixture.whenStable();
    expect(list).toHaveBeenCalledTimes(3);
    expect(await harness.summary()).toContain('2');
  });

  it('clears the accepted URL cursor only after a failed Previous retry succeeds', async () => {
    const fixture = TestBed.createComponent(AuthoringCaseTableComponent);
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, PagingQaHarness);
    list.mockResolvedValueOnce({ items: [row('middle')], next_cursor: null });
    await harness.click('next');
    query.next(convertToParamMap({ cursor: 'second', q: 'kept-filter', limit: '20' }));
    await fixture.whenStable();
    navigate.mockClear();
    list.mockRejectedValueOnce(
      new ApiRequestError({ ...apiError('network', 0).error, retryable: true }),
    );
    await harness.click('previous');
    expect(navigate).not.toHaveBeenCalled();
    list.mockResolvedValueOnce({ items: [row('first')], next_cursor: 'second' });
    await harness.click('retry');
    expect(list.mock.lastCall?.[1]).not.toHaveProperty('cursor');
    expect(navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParamsHandling: 'merge',
        queryParams: { cursor: null },
        replaceUrl: true,
      }),
    );
    expect(await harness.summary()).toContain('1');
  });
});
