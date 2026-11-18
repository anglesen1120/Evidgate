import { ComponentHarness, manualChangeDetection } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import type { CaseListResponse } from '@evidgate/sdk';
import { BehaviorSubject, firstValueFrom, Subject } from 'rxjs';
import { ApiRequestError } from '../../core/http/api-error';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { CaseListDataService } from '../../core/data/case-list-data.service';
import { CASE_LIST_FIXTURE, CATALOG_FAILURE } from './case-list-store.fixtures';
import { CATALOG_OMITTED_COLUMNS } from './case-list-columns';
import { CaseListPage } from './case-list.page';

class CatalogPageHarness extends ComponentHarness {
  static hostSelector = 'app-case-list-page';
  readonly headings = this.locatorForAll('[role="columnheader"]');
  readonly table = this.locatorForOptional('app-data-table');
  readonly loading = this.locatorForOptional('app-loading-pattern');
  readonly empty = this.locatorForOptional('app-empty-pattern');
  readonly error = this.locatorForOptional('app-error-pattern');
  readonly forbidden = this.locatorForOptional('app-forbidden-pattern');
  readonly more = this.locatorForOptional('[data-catalog-more]');
  readonly summary = this.locatorFor('[data-catalog-summary]');
  readonly status = this.locatorFor('#catalog-status');
  async text() {
    return (await this.host()).text();
  }
  async columnLabels() {
    return Promise.all((await this.headings()).map((heading) => heading.text()));
  }
}

describe('CaseListPage', () => {
  provideLayoutTestStubs();
  let list = vi.fn<CaseListDataService['list']>();
  const query = new BehaviorSubject(convertToParamMap({}));
  beforeEach(() => {
    query.next(convertToParamMap({}));
    list = vi.fn<CaseListDataService['list']>().mockResolvedValue(CASE_LIST_FIXTURE);
    const paramMap = new BehaviorSubject(convertToParamMap({ projectId: 'project' }));
    TestBed.configureTestingModule({
      imports: [CaseListPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: CaseListDataService, useValue: { list } },
        {
          provide: ActivatedRoute,
          useValue: { pathFromRoot: [{ paramMap }], queryParamMap: query },
        },
      ],
    });
  });
  async function render() {
    const fixture = TestBed.createComponent(CaseListPage);
    fixture.detectChanges();
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, CatalogPageHarness);
    return { fixture, harness };
  }

  it('renders only contracted columns and fixture values with an honest continuation notice', async () => {
    const { harness } = await render();
    expect(await harness.columnLabels()).toEqual([
      'Identity / name',
      'Framework',
      'Status',
      'Owner membership',
    ]);
    const text = await harness.text();
    expect(await (await harness.status()).text()).toContain('Active and missing');
    for (const row of CASE_LIST_FIXTURE.items) {
      expect(text).toContain(row.annotation_id);
      expect(text).toContain(row.name);
      expect(text).toContain(row.framework);
    }
    expect(text).toContain(CASE_LIST_FIXTURE.items[0]?.owner_membership_id);
    expect(await harness.more()).not.toBeNull();
    expect(CATALOG_OMITTED_COLUMNS).toEqual([
      { column: 'revision', missingField: 'CaseDto.current_revision_id' },
      { column: 'platform', missingField: 'CaseDto.platform' },
      { column: 'level', missingField: 'CaseDto.level' },
      { column: 'purpose', missingField: 'CaseDto.purpose' },
      {
        column: 'rename history',
        missingField: 'CaseDto.renames (only CaseDetailDto has renames)',
      },
      { column: 'linked manual case', missingField: 'CaseDto.linked_manual_case_id' },
      { column: 'script status', missingField: 'CaseDto.script_status' },
    ]);
    for (const omitted of CATALOG_OMITTED_COLUMNS)
      expect(await harness.columnLabels()).not.toContain(omitted.column);
  });

  it('renders a loading pattern while the read is pending', async () => {
    const pending = new Subject<CaseListResponse>();
    list.mockReturnValue(firstValueFrom(pending));
    const fixture = TestBed.createComponent(CaseListPage);
    fixture.detectChanges();
    TestBed.tick();
    await manualChangeDetection(async () => {
      const loading = await TestbedHarnessEnvironment.harnessForFixture(
        fixture,
        CatalogPageHarness,
      );
      expect(await loading.loading()).not.toBeNull();
    });
    pending.next(CASE_LIST_FIXTURE);
    await fixture.whenStable();
  });

  it('renders the genuine empty state without a continuation or table', async () => {
    list.mockResolvedValue({ items: [], next_cursor: null });
    const { harness } = await render();
    expect(await harness.empty()).not.toBeNull();
    expect(await harness.text()).toContain('No test identities in this project');
    expect(await harness.table()).toBeNull();
    expect(await harness.more()).toBeNull();
  });

  it('renders a distinct no-match state and preserves the server continuation notice', async () => {
    const { fixture, harness } = await render();
    const summary = await harness.summary();
    expect(await summary.getAttribute('role')).toBe('status');
    expect(await summary.text()).toBe('Matching identities in this page: 2');
    expect(await summary.text()).not.toContain('018f0000-0000-7000-8000-0000000c0017');
    query.next(convertToParamMap({ q: 'not-present' }));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(await summary.text()).toBe('No matching identities in this page.');
    expect(list).toHaveBeenCalledTimes(1);
    expect(await harness.text()).toContain('No matching identities in this page');
    expect(await harness.more()).not.toBeNull();
    expect(await harness.table()).toBeNull();
  });

  it('renders a recoverable error rather than a successful empty list', async () => {
    list.mockRejectedValue(new ApiRequestError(CATALOG_FAILURE));
    const { harness } = await render();
    expect(await harness.error()).not.toBeNull();
    expect(await harness.empty()).toBeNull();
    expect(await harness.table()).toBeNull();
  });

  it('renders forbidden without revealing earlier case or owner data', async () => {
    const { fixture, harness } = await render();
    const previous = CASE_LIST_FIXTURE.items[0];
    if (!previous?.owner_membership_id) throw new Error('No owned case fixture');
    expect(await harness.text()).toContain(previous.annotation_id);
    expect(await harness.text()).toContain(previous.owner_membership_id);
    const pending = new Subject<CaseListResponse>();
    list.mockReturnValueOnce(firstValueFrom(pending));
    query.next(convertToParamMap({ framework: 'vitest' }));
    fixture.detectChanges();
    TestBed.tick();
    await manualChangeDetection(async () => {
      expect(list).toHaveBeenCalledTimes(2);
      expect(list).toHaveBeenLastCalledWith(
        'project',
        { framework: 'vitest', limit: 50 },
        expect.any(AbortSignal),
      );
      expect(await harness.loading()).not.toBeNull();
      expect(await harness.text()).not.toContain(previous.annotation_id);
      expect(await harness.text()).not.toContain(previous.owner_membership_id);
      expect(await harness.table()).toBeNull();
    });
    pending.error(
      new ApiRequestError({ ...CATALOG_FAILURE, kind: 'forbidden', status: 403, retryable: false }),
    );
    await fixture.whenStable();
    expect(await harness.forbidden()).not.toBeNull();
    expect(await harness.text()).not.toContain(previous.annotation_id);
    expect(await harness.text()).not.toContain(previous.owner_membership_id);
    expect(await harness.table()).toBeNull();
  });
});
