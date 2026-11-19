import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { CaseListDataService } from '../../core/data/case-list-data.service';
import { SessionStore } from '../../core/session/session.store';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { CatalogMembersReader } from './catalog-members.reader';
import { CASE_LIST_FIXTURE } from './case-list-store.fixtures';
import { CaseListPage } from './case-list.page';
import { CaseListStore } from './case-list.store';

class CatalogBoundsHarness extends ComponentHarness {
  static hostSelector = 'app-case-list-page';
  readonly notice = this.locatorForOptional('[data-catalog-browse-capped]');
  readonly more = this.locatorForOptional('[data-catalog-load-more]');
  readonly empty = this.locatorForOptional('app-empty-pattern');
}

describe('catalog retained-row cap notice', () => {
  provideLayoutTestStubs();
  const list = vi.fn<CaseListDataService['list']>();
  const query = new BehaviorSubject(convertToParamMap({}));
  beforeEach(() => {
    list.mockReset();
    query.next(convertToParamMap({}));
    const paramMap = new BehaviorSubject(convertToParamMap({ projectId: 'project', orgId: 'org' }));
    TestBed.configureTestingModule({
      imports: [CaseListPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: CaseListDataService, useValue: { list } },
        { provide: SessionStore, useValue: { has: () => false } },
        {
          provide: ActivatedRoute,
          useValue: { pathFromRoot: [{ paramMap }], queryParamMap: query },
        },
      ],
    });
    TestBed.overrideComponent(CaseListPage, {
      add: {
        providers: [{
          provide: CatalogMembersReader,
          useValue: {
            members: signal(new Map()),
            status: signal('ready'),
            failure: signal(null),
            init: vi.fn(),
            clear: vi.fn(),
            reload: vi.fn(),
          },
        }],
      },
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  async function render(count: number, cursor: string | null) {
    const first = CASE_LIST_FIXTURE.items[0];
    if (!first) throw new Error('Missing case fixture');
    list.mockResolvedValueOnce({
      items: Array.from({ length: count }, (_, index) => ({ ...first, id: String(index) })),
      next_cursor: cursor,
    });
    const fixture = TestBed.createComponent(CaseListPage);
    fixture.detectChanges();
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, CatalogBoundsHarness);
    return { fixture, harness };
  }

  it('shows a persistent catalog cap notice and removes forward paging at cap plus one', async () => {
    const { fixture, harness } = await render(10_001, 'beyond-cap');
    const notice = await harness.notice();
    expect(notice).not.toBeNull();
    expect(await notice?.text()).toContain('Case list is at its load limit');
    expect(await notice?.text()).toContain('status or framework filters');
    expect(await notice?.text()).toContain('10,000');
    expect(await harness.more()).toBeNull();

    query.next(convertToParamMap({ q: 'no-loaded-case-has-this-name' }));
    expect(await harness.empty()).not.toBeNull();
    expect(await harness.notice()).not.toBeNull();
    expect(list).toHaveBeenCalledTimes(1);

    list.mockResolvedValueOnce(CASE_LIST_FIXTURE);
    fixture.debugElement.injector.get(CaseListStore).reload();
    await fixture.whenStable();
    expect(await harness.notice()).toBeNull();
  });

  it('does not show a cap notice for exactly 10000 complete catalog rows', async () => {
    const { harness } = await render(10_000, null);
    expect(await harness.notice()).toBeNull();
    expect(await harness.more()).toBeNull();
  });
});
