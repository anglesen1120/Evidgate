import { ComponentHarness, manualChangeDetection } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import {
  ActivatedRoute,
  convertToParamMap,
  provideRouter,
  Router,
  type NavigationExtras,
} from '@angular/router';
import type { ManualCaseListItemDto, ManualCaseListResponse } from '@evidgate/sdk';
import { BehaviorSubject, firstValueFrom, Subject } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiRequestError } from '../../core/http/api-error';
import { apiError } from '../../shared/testing/http-data.testing';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import { AuthoringDataService } from './authoring-data.service';
import { AuthoringCaseTableComponent } from './authoring-case-table.component';

class TableHarness extends ComponentHarness {
  static hostSelector = 'app-authoring-case-table';
  readonly search = this.locatorFor('#authoring-case-search');
  readonly item = this.locatorFor('#authoring-case-item');
  readonly itemError = this.locatorFor('#authoring-case-item-error');
  readonly searchError = this.locatorFor('#authoring-case-search-error');
  readonly apply = this.locatorFor('[data-case-apply]');
  readonly source = this.locatorFor('#authoring-case-source');
  readonly owner = this.locatorFor('#authoring-case-owner');
  readonly loadMore = this.locatorFor('[data-case-load-more]');
  readonly refresh = this.locatorFor('[data-case-refresh]');
  readonly empty = this.locatorForOptional('app-empty-pattern');
  readonly loading = this.locatorForOptional('app-loading-pattern');
  readonly forbidden = this.locatorForOptional('app-forbidden-pattern');
  readonly error = this.locatorForOptional('app-error-pattern');
  readonly wait = this.locatorForOptional('app-retry-after');
  readonly retry = this.locatorFor('app-retry-after button');
  readonly clearEmpty = this.locatorFor('app-empty-pattern button');
}

const caseRow = (id: string): ManualCaseListItemDto => ({
  case_id: id,
  code: `SHOP-${id}`,
  node_id: '018f0000-0000-7000-8000-000000000101',
  checklist_item_id: '018f0000-0000-7000-8000-000000000201',
  title: 'Địa chỉ giao hàng',
  priority: 'high',
  state: 'draft',
  version: 1,
  linked_live_count: 0,
  linked_case_ids: [],
});

describe('AuthoringCaseTableComponent', () => {
  provideLayoutTestStubs();
  const query = new BehaviorSubject(convertToParamMap({}));
  const list = vi.fn<AuthoringDataService['listManualCases']>();
  const navigate =
    vi.fn<(commands: readonly unknown[], extras?: NavigationExtras) => Promise<boolean>>();
  let fixture: ComponentFixture<AuthoringCaseTableComponent>;
  beforeEach(() => {
    query.next(convertToParamMap({}));
    navigate.mockClear();
    list.mockReset().mockResolvedValue({ items: [], next_cursor: null });
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
  async function mount() {
    fixture = TestBed.createComponent(AuthoringCaseTableComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    return TestbedHarnessEnvironment.harnessForFixture(fixture, TableHarness);
  }

  it('renders loading while a real request remains pending', async () => {
    const pending = new Subject<ManualCaseListResponse>();
    list.mockReturnValueOnce(firstValueFrom(pending));
    const fixture = TestBed.createComponent(AuthoringCaseTableComponent);
    fixture.detectChanges();
    await manualChangeDetection(async () => {
      const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, TableHarness);
      expect(await harness.loading()).not.toBeNull();
    });
    pending.next({ items: [], next_cursor: null });
    await fixture.whenStable();
  });

  it('distinguishes empty project and empty selected category', async () => {
    const harness = await mount();
    expect(await (await harness.empty())?.text()).toContain('project has no manual cases');
    query.next(convertToParamMap({ node_id: '018f0000-0000-7000-8000-000000000101' }));
    expect(await (await harness.empty())?.text()).toContain('category has no test cases');
  });

  it('renders no-match with a reachable Clear filters action and keeps the search', async () => {
    query.next(convertToParamMap({ q: 'unmatched' }));
    const harness = await mount();
    expect(await (await harness.empty())?.text()).toContain('No test cases match');
    expect(await (await harness.search()).getProperty('value')).toBe('unmatched');
    expect(await (await harness.clearEmpty()).text()).toContain('Clear filters');
  });

  it('restores draft controls and server filters on route back/forward emissions', async () => {
    const harness = await mount();
    query.next(convertToParamMap({ q: 'first', priority: 'high', covered: 'false' }));
    expect(await (await harness.search()).getProperty('value')).toBe('first');
    expect(list.mock.lastCall?.[1]).toMatchObject({ q: 'first', priority: 'high', covered: false });
    query.next(convertToParamMap({ q: 'second', state: 'approved' }));
    expect(await (await harness.search()).getProperty('value')).toBe('second');
    query.next(convertToParamMap({ q: 'first', priority: 'high', covered: 'false' }));
    expect(await (await harness.search()).getProperty('value')).toBe('first');
    expect(list.mock.lastCall?.[1]).toMatchObject({ q: 'first', priority: 'high', covered: false });
  });

  it('renders forbidden without rows and generic errors without an empty-success message', async () => {
    list.mockRejectedValueOnce(apiError('forbidden', 403));
    const harness = await mount();
    expect(await harness.forbidden()).not.toBeNull();
    expect(await harness.empty()).toBeNull();
    list.mockRejectedValueOnce(apiError('network', 0));
    query.next(convertToParamMap({ q: 'different' }));
    expect(await harness.error()).not.toBeNull();
  });

  it('renders the supplied 503 Retry-After and disables retries until the deadline', async () => {
    const unavailable = apiError('unavailable', 503);
    list.mockRejectedValueOnce(
      new ApiRequestError({ ...unavailable.error, retryable: true, retryAfterSeconds: 30 }),
    );
    const harness = await mount();
    expect(await harness.wait()).not.toBeNull();
    expect(await (await harness.retry()).getAttribute('disabled')).not.toBeNull();
    expect(await (await harness.host()).text()).not.toContain('project has no manual cases');
  });

  it('disables unsupported source and owner controls with persistent announced reasons', async () => {
    const harness = await mount();
    expect(await (await harness.source()).getAttribute('disabled')).not.toBeNull();
    expect(await (await harness.owner()).getAttribute('disabled')).not.toBeNull();
    expect(await (await harness.source()).getAttribute('aria-describedby')).toBe(
      'authoring-source-help',
    );
    expect(await (await harness.host()).text()).toContain('Source filtering is unavailable');
    expect(await (await harness.host()).text()).toContain('Owner filtering is unavailable');
  });

  it('announces invalid checklist UUIDs instead of silently disabling a filter action', async () => {
    const harness = await mount();
    await (await harness.item()).sendKeys('not-a-uuid');
    expect(await (await harness.item()).getAttribute('aria-invalid')).toBe('true');
    expect(await (await harness.itemError()).text()).toContain('complete checklist UUID');
    expect(await (await harness.apply()).getAttribute('disabled')).not.toBeNull();
  });

  it('accepts 200 Unicode characters but announces and refuses a 201-character query', async () => {
    query.next(convertToParamMap({ q: '😀'.repeat(200) }));
    const harness = await mount();
    expect(list.mock.lastCall?.[1]?.q).toBe('😀'.repeat(200));
    list.mockClear();
    query.next(convertToParamMap({ q: '😀'.repeat(201) }));
    expect(await (await harness.searchError()).text()).toContain('at most 200');
    expect(await (await harness.apply()).getAttribute('disabled')).not.toBeNull();
    expect(list).not.toHaveBeenCalled();
  });

  it('replaces history on a cursor step and keeps the unapplied search draft', async () => {
    list
      .mockReset()
      .mockResolvedValueOnce({ items: [caseRow('1')], next_cursor: 'second' })
      .mockResolvedValueOnce({ items: [caseRow('2')], next_cursor: null });
    const harness = await mount();
    await (await harness.search()).sendKeys('unapplied');
    await (await harness.loadMore()).click();
    await vi.waitFor(() => expect(navigate).toHaveBeenCalled());
    const [commands, extras] = navigate.mock.calls[0] ?? [];
    expect(commands).toEqual([]);
    expect(extras?.queryParams).toEqual({ cursor: 'second' });
    expect(extras?.queryParamsHandling).toBe('merge');
    expect(extras?.replaceUrl).toBe(true);
    query.next(convertToParamMap({ cursor: 'second' }));
    expect(await (await harness.search()).getProperty('value')).toBe('unapplied');
  });

  it('refreshes from the first page, announces it and drops the page cursor', async () => {
    query.next(convertToParamMap({ cursor: 'second' }));
    list
      .mockReset()
      .mockResolvedValueOnce({ items: [caseRow('2')], next_cursor: null })
      .mockResolvedValueOnce({ items: [caseRow('1')], next_cursor: 'second' });
    const harness = await mount();
    expect(list.mock.calls[0]?.[1]).toMatchObject({ cursor: 'second' });
    expect(await (await harness.host()).text()).toContain(
      'Refresh reloads this list from the first page',
    );
    await (await harness.refresh()).click();
    await vi.waitFor(() => expect(navigate).toHaveBeenCalled());
    const [, extras] = navigate.mock.calls[0] ?? [];
    expect(extras?.queryParams).toEqual({ cursor: null });
    expect(extras?.replaceUrl).toBe(true);
    await vi.waitFor(() => expect(list).toHaveBeenCalledTimes(2));
    expect(list.mock.lastCall?.[1]).not.toHaveProperty('cursor');
  });
});
