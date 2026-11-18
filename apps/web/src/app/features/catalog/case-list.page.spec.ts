import { manualChangeDetection } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import type { CaseDto, CaseListResponse, MembershipView } from '@evidgate/sdk';
import { BehaviorSubject, firstValueFrom, Subject } from 'rxjs';
import { ApiRequestError, type ApiError } from '../../core/http/api-error';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { CaseListDataService } from '../../core/data/case-list-data.service';
import { SessionStore } from '../../core/session/session.store';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import { CatalogMembersReader } from './catalog-members.reader';
import { CASE_LIST_FIXTURE, CATALOG_FAILURE } from './case-list-store.fixtures';
import { CATALOG_OMITTED_COLUMNS } from './case-list-columns';
import { CaseListPage } from './case-list.page';
import { CatalogPageHarness } from './case-list-page.testing';

describe('CaseListPage', () => {
  provideLayoutTestStubs();
  let list = vi.fn<CaseListDataService['list']>();
  let update = vi.fn<CaseListDataService['update']>();
  const canWrite = signal(true);
  const wide = signal(true);
  const membersStatus = signal<'loading' | 'ready' | 'error'>('ready');
  const membersFailure = signal<ApiError | null>(null);
  const membership = (id: string, email: string): MembershipView => ({
    id,
    invited_email: email,
    org_id: 'org',
    status: 'active',
    roles: [],
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z',
    version: 1,
  });
  const original = CASE_LIST_FIXTURE.items[0];
  if (!original?.owner_membership_id) throw new Error('No owned catalog fixture');
  const firstCase = original;
  const originalOwnerId = original.owner_membership_id;
  const nextOwner = membership('membership-next', 'next.owner@example.test');
  const memberships = signal<ReadonlyMap<string, MembershipView>>(
    new Map([
      [originalOwnerId, membership(originalOwnerId, 'current.owner@example.test')],
      [nextOwner.id, nextOwner],
    ]),
  );
  const defaultMemberships = memberships();
  const query = new BehaviorSubject(convertToParamMap({}));
  beforeEach(() => {
    query.next(convertToParamMap({}));
    canWrite.set(true);
    wide.set(true);
    memberships.set(defaultMemberships);
    membersStatus.set('ready');
    membersFailure.set(null);
    update = vi.fn<CaseListDataService['update']>().mockResolvedValue({
      ...firstCase,
      owner_membership_id: nextOwner.id,
      version: firstCase.version + 1,
    });
    list = vi.fn<CaseListDataService['list']>().mockResolvedValue(CASE_LIST_FIXTURE);
    const paramMap = new BehaviorSubject(convertToParamMap({ projectId: 'project', orgId: 'org' }));
    TestBed.configureTestingModule({
      imports: [CaseListPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: CaseListDataService, useValue: { list, update } },
        {
          provide: SessionStore,
          useValue: { has: (permission: string) => permission !== 'case:write' || canWrite() },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: { canMutate: wide, allowsMutation: () => wide() },
        },
        {
          provide: ActivatedRoute,
          useValue: { pathFromRoot: [{ paramMap }], queryParamMap: query },
        },
      ],
    });
    TestBed.overrideComponent(CaseListPage, {
      add: {
        providers: [
          {
            provide: CatalogMembersReader,
            useValue: {
              members: memberships,
              status: membersStatus,
              failure: membersFailure,
              init: vi.fn(),
              clear: vi.fn(),
              reload: vi.fn(),
            },
          },
        ],
      },
    });
  });
  afterEach(() => vi.restoreAllMocks());
  async function render() {
    const fixture = TestBed.createComponent(CaseListPage);
    fixture.detectChanges();
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, CatalogPageHarness);
    return { fixture, harness };
  }

  it('renders contracted columns, member email and an honest loaded count', async () => {
    const { harness } = await render();
    expect(await harness.columnLabels()).toEqual([
      'Identity / name',
      'Framework',
      'Status',
      'Owner',
    ]);
    const text = await harness.text();
    expect(await (await harness.status()).text()).toContain('Active and missing');
    for (const row of CASE_LIST_FIXTURE.items) {
      expect(text).toContain(row.annotation_id);
      expect(text).toContain(row.name);
      expect(text).toContain(row.framework);
    }
    expect(text).toContain('current.owner@example.test');
    expect(await (await harness.loaded()).text()).toContain('2 loaded');
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

  it('searches only accumulated rows and keeps Load more available for a no-match', async () => {
    const { fixture, harness } = await render();
    const summary = await harness.summary();
    expect(await summary.getAttribute('role')).toBe('status');
    expect(await summary.text()).toContain('2 loaded');
    expect(await summary.text()).toContain('2 matching identities');
    expect(await summary.text()).not.toContain('018f0000-0000-7000-8000-0000000c0017');
    query.next(convertToParamMap({ q: 'not-present' }));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(await summary.text()).toContain('2 loaded');
    expect(await summary.text()).toContain('No matching identities in loaded results.');
    expect(list).toHaveBeenCalledTimes(1);
    expect(await harness.text()).toContain('No matching identities in loaded results');
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
    expect(await harness.text()).toContain('current.owner@example.test');
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

  it('optimistically assigns an owner, shows pending, and confirms the server version', async () => {
    const pending = new Subject<CaseDto>();
    update.mockReturnValueOnce(firstValueFrom(pending));
    const { fixture, harness } = await render();
    await manualChangeDetection(async () => {
      await harness.chooseOwner(firstCase.id, nextOwner.invited_email);
      fixture.detectChanges();
      TestBed.tick();
      expect(await (await harness.ownerTrigger(firstCase.id)).text()).toContain(
        nextOwner.invited_email,
      );
      expect(await harness.ownerPending()).not.toBeNull();
    });
    pending.next({
      ...firstCase,
      owner_membership_id: nextOwner.id,
      version: firstCase.version + 1,
    });
    await fixture.whenStable();
    expect(await harness.ownerPending()).toBeNull();
    expect(await harness.ownerFailure()).toBeNull();
    expect(await (await harness.ownerTrigger(firstCase.id)).text()).toContain(
      nextOwner.invited_email,
    );
    expect(update).toHaveBeenCalledWith(
      firstCase.id,
      { owner_membership_id: nextOwner.id },
      expect.any(AbortSignal),
      `"${firstCase.version}"`,
    );
  });

  it.each([403, 409, 412, 422])(
    'rolls back an owner PATCH refused with %s and explains retry',
    async (status) => {
      update.mockRejectedValueOnce(
        new ApiRequestError({
          ...CATALOG_FAILURE,
          status,
          kind:
            status === 403
              ? 'forbidden'
              : status === 412
                ? 'precondition_failed'
                : status === 422
                  ? 'validation'
                  : 'conflict',
          code:
            status === 403
              ? 'forbidden'
              : status === 412
                ? 'precondition_failed'
                : status === 422
                  ? 'validation'
                  : 'conflict',
          retryable: false,
        }),
      );
      const { harness } = await render();
      await harness.chooseOwner(firstCase.id, nextOwner.invited_email);
      expect(await (await harness.ownerTrigger(firstCase.id)).text()).toContain(
        'current.owner@example.test',
      );
      const error = await harness.ownerFailure();
      expect(await error?.text()).toContain('previous owner');
      expect(await error?.text()).toContain(status === 403 ? 'permission' : 'Reload');
      expect(await harness.ownerPending()).toBeNull();
    },
  );

  it('disables owner assignment with a visible reason without case write permission', async () => {
    canWrite.set(false);
    const { harness } = await render();
    expect(await (await harness.ownerTrigger(firstCase.id)).getProperty('disabled')).toBe(true);
    expect(await harness.text()).toContain('case:write is required to assign an owner.');
    expect(update).not.toHaveBeenCalled();
  });

  it('keeps owner details readable but disables mutation below the desktop breakpoint', async () => {
    wide.set(false);
    const { harness } = await render();
    expect(await (await harness.ownerTrigger(firstCase.id)).getProperty('disabled')).toBe(true);
    expect(await harness.text()).toContain('at least 768 pixels wide');
    expect(await harness.text()).toContain('current.owner@example.test');
    expect(update).not.toHaveBeenCalled();
  });

  it('keeps existing ownership readable while member choices load or fail', async () => {
    membersStatus.set('loading');
    const { fixture, harness } = await render();
    expect(await (await harness.ownerTrigger(firstCase.id)).getProperty('disabled')).toBe(true);
    expect(await harness.text()).toContain('Loading owner choices');
    expect(await harness.text()).toContain('current.owner@example.test');
    membersStatus.set('error');
    membersFailure.set(CATALOG_FAILURE);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(await harness.text()).toContain('Owner choices could not be loaded');
    expect(await harness.text()).toContain('Retry owner choices');
    expect(update).not.toHaveBeenCalled();
  });

  it('does not offer an unassign action when active member choices are empty', async () => {
    memberships.set(new Map());
    const { harness } = await render();
    const picker = await harness.ownerTrigger(firstCase.id);
    expect(await picker.getProperty('disabled')).toBe(true);
    expect(await picker.text()).toContain('Member details unavailable');
    expect(await harness.text()).toContain('No active members are available to assign');
    expect(update).not.toHaveBeenCalled();
  });

  it('appends the next cursor page, announces the count and focuses its first row', async () => {
    const appended = {
      ...firstCase,
      id: 'appended-case',
      annotation_id: 'appended-identity',
      name: 'Appended identity',
    };
    list
      .mockResolvedValueOnce(CASE_LIST_FIXTURE)
      .mockResolvedValueOnce({ items: [appended], next_cursor: null });
    const { fixture, harness } = await render();
    await (await harness.more())?.click();
    await fixture.whenStable();
    expect(await harness.rows()).toHaveLength(3);
    expect(await (await harness.loaded()).text()).toContain('3 loaded');
    expect(await (await harness.summary()).text()).toContain('3 loaded');
    const rows = await harness.rows();
    expect(await rows[2]?.getAttribute('data-row-id')).toBe(appended.id);
    expect(await rows[2]?.isFocused()).toBe(true);
    expect(list).toHaveBeenLastCalledWith(
      'project',
      { cursor: CASE_LIST_FIXTURE.next_cursor, limit: 50 },
      expect.any(AbortSignal),
    );
  });

  it('focuses the loaded count when local search hides every appended row', async () => {
    const appended = {
      ...firstCase,
      id: 'appended-case',
      annotation_id: 'appended-identity',
      name: 'Unrelated identity',
    };
    list
      .mockResolvedValueOnce(CASE_LIST_FIXTURE)
      .mockResolvedValueOnce({ items: [appended], next_cursor: null });
    const { fixture, harness } = await render();
    query.next(convertToParamMap({ q: 'Checkout' }));
    fixture.detectChanges();
    await fixture.whenStable();
    await (await harness.more())?.click();
    await fixture.whenStable();
    expect(await harness.rows()).toHaveLength(1);
    expect(await (await harness.loaded()).text()).toContain('3 loaded');
    expect(await (await harness.loaded()).isFocused()).toBe(true);
    expect(await (await harness.summary()).text()).toContain('3 loaded');
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('resets accumulated rows and the cursor when a server filter changes', async () => {
    const appended = { ...firstCase, id: 'appended-case', annotation_id: 'appended-identity' };
    list
      .mockResolvedValueOnce(CASE_LIST_FIXTURE)
      .mockResolvedValueOnce({ items: [appended], next_cursor: 'next-page' })
      .mockResolvedValueOnce({ items: [firstCase], next_cursor: null });
    const { fixture, harness } = await render();
    await (await harness.more())?.click();
    query.next(convertToParamMap({ framework: 'vitest' }));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(await harness.rows()).toHaveLength(1);
    expect(await (await harness.loaded()).text()).toContain('1 loaded');
    expect(list).toHaveBeenLastCalledWith(
      'project',
      { framework: 'vitest', limit: 50 },
      expect.any(AbortSignal),
    );
  });

  it('retains accumulated rows and offers a retry when loading another page fails', async () => {
    list
      .mockResolvedValueOnce(CASE_LIST_FIXTURE)
      .mockRejectedValueOnce(new ApiRequestError(CATALOG_FAILURE));
    const { harness } = await render();
    await (await harness.more())?.click();
    expect(await harness.rows()).toHaveLength(2);
    expect(await (await harness.loaded()).text()).toContain('2 loaded');
    expect(await (await harness.pageError())?.text()).toContain('Loaded results are unchanged');
    expect(await (await harness.pageError())?.text()).toContain('Retry loading more');
  });

  it('resets accumulated rows and persists page size without retaining a cursor', async () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const appended = { ...firstCase, id: 'appended-case', annotation_id: 'appended-identity' };
    list
      .mockResolvedValueOnce(CASE_LIST_FIXTURE)
      .mockResolvedValueOnce({ items: [appended], next_cursor: 'more' })
      .mockResolvedValue(CASE_LIST_FIXTURE);
    const { harness } = await render();
    await (await harness.more())?.click();
    expect(await harness.rows()).toHaveLength(3);
    await harness.choosePageSize(25);
    expect(await harness.rows()).toHaveLength(2);
    expect(list).toHaveBeenLastCalledWith('project', { limit: 25 }, expect.any(AbortSignal));
    expect(navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({
        queryParamsHandling: 'merge',
        queryParams: { limit: 25, cursor: null },
      }),
    );
  });

  it('uses the shared virtual viewport above 200 accumulated rows', async () => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1280);
    list.mockResolvedValue({
      items: Array.from({ length: 201 }, (_, index) => ({
        ...firstCase,
        id: `case-${index}`,
        annotation_id: `identity-${index}`,
        name: `Identity ${index}`,
      })),
      next_cursor: null,
    });
    const { harness } = await render();
    expect(await harness.viewport()).not.toBeNull();
    const rows = await harness.rows();
    expect(rows.length).toBeGreaterThan(0);
    expect(rows.length).toBeLessThan(201);
    expect(await (await harness.loaded()).text()).toContain('201 loaded');
  });
});
