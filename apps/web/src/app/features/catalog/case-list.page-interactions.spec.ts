import { manualChangeDetection } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import type { CaseListResponse, MembershipView } from '@evidgate/sdk';
import { BehaviorSubject, firstValueFrom, Subject } from 'rxjs';
import { CaseListDataService } from '../../core/data/case-list-data.service';
import { ApiRequestError } from '../../core/http/api-error';
import { SessionStore } from '../../core/session/session.store';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import { CatalogMembersReader } from './catalog-members.reader';
import { CatalogPageHarness } from './case-list-page.testing';
import { CASE_LIST_FIXTURE, CATALOG_FAILURE } from './case-list-store.fixtures';
import { CaseListPage } from './case-list.page';
import { CaseListStore } from './case-list.store';

const firstCase = CASE_LIST_FIXTURE.items[0];
if (!firstCase?.owner_membership_id) throw new Error('No owned catalog fixture');
const ownedCase = firstCase;
const owner: MembershipView = {
  id: firstCase.owner_membership_id,
  invited_email: 'owner@example.test',
  org_id: 'org',
  status: 'active',
  roles: [],
  created_at: '2026-01-01T00:00:00Z',
  updated_at: '2026-01-01T00:00:00Z',
  version: 1,
};
const nextOwner = { ...owner, id: 'next-owner', invited_email: 'next@example.test' };
const appended = { ...ownedCase, id: 'appended-case', annotation_id: 'appended-identity' };

describe('Catalog permission and asynchronous interaction boundaries', () => {
  provideLayoutTestStubs();
  const canRead = signal(true);
  const memberships = signal<ReadonlyMap<string, MembershipView>>(new Map());
  const memberStatus = signal<'ready' | 'error'>('ready');
  const memberInit = vi.fn();
  const memberClear = vi.fn(() => {
    memberships.set(new Map());
    memberStatus.set('ready');
  });
  let list = vi.fn<CaseListDataService['list']>();
  let update = vi.fn<CaseListDataService['update']>();

  beforeEach(() => {
    canRead.set(true);
    memberStatus.set('ready');
    memberInit.mockClear();
    memberClear.mockClear();
    memberships.set(
      new Map([
        [owner.id, owner],
        [nextOwner.id, nextOwner],
      ]),
    );
    list = vi.fn<CaseListDataService['list']>().mockResolvedValue(CASE_LIST_FIXTURE);
    update = vi.fn<CaseListDataService['update']>();
    const params = new BehaviorSubject(convertToParamMap({ orgId: 'org', projectId: 'project' }));
    TestBed.configureTestingModule({
      imports: [CaseListPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: CaseListDataService, useValue: { list, update } },
        {
          provide: SessionStore,
          useValue: { has: (p: string) => p !== 'membership:read' || canRead() },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: { canMutate: () => true, allowsMutation: () => true },
        },
        {
          provide: ActivatedRoute,
          useValue: {
            pathFromRoot: [{ paramMap: params }],
            queryParamMap: new BehaviorSubject(convertToParamMap({})),
          },
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
              status: memberStatus,
              failure: () => CATALOG_FAILURE,
              init: memberInit,
              clear: memberClear,
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
    const store = fixture.debugElement.injector.get(CaseListStore);
    return { fixture, harness, store };
  }

  it('does not request member choices or show a failure banner without membership read', async () => {
    canRead.set(false);
    memberStatus.set('error');
    const { harness } = await render();
    expect(memberInit).not.toHaveBeenCalled();
    expect(memberClear).toHaveBeenCalled();
    expect(await harness.text()).toContain(owner.id);
    expect(await harness.text()).toContain('No active members are available to assign');
    expect(await harness.text()).not.toContain('Retry owner choices');
    expect(await (await harness.ownerTrigger(ownedCase.id)).getProperty('disabled')).toBe(true);
  });

  it('clears member details when read permission is revoked without a route change', async () => {
    const { fixture, harness } = await render();
    expect(memberInit).toHaveBeenCalledWith('org');
    expect(await (await harness.ownerTrigger(ownedCase.id)).getProperty('disabled')).toBe(false);
    canRead.set(false);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(memberClear).toHaveBeenCalled();
    expect(await harness.text()).not.toContain(owner.invited_email);
    expect(await harness.text()).toContain(owner.id);
    expect(await (await harness.ownerTrigger(ownedCase.id)).getProperty('disabled')).toBe(true);
  });

  it('clears and cancels the member reader when the page is destroyed', async () => {
    const { fixture } = await render();
    memberClear.mockClear();
    fixture.destroy();
    expect(memberClear).toHaveBeenCalledExactlyOnceWith();
    expect(memberships().size).toBe(0);
  });
  it.each(['pending', 'render'] as const)(
    'keeps Search focused when focus moves during the %s part of Load more',
    async (timing) => {
      const pending = new Subject<CaseListResponse>();
      list.mockResolvedValueOnce(CASE_LIST_FIXTURE).mockReturnValueOnce(firstValueFrom(pending));
      const { fixture, harness } = await render();
      await manualChangeDetection(async () => {
        const more = await harness.more();
        await more?.focus();
        await more?.click();
        if (timing === 'pending') await (await harness.search()).focus();
        pending.next({ items: [appended], next_cursor: null });
        if (timing === 'render') {
          await Promise.resolve();
          await Promise.resolve();
          await (await harness.search()).focus();
        }
      });
      await fixture.whenStable();
      expect(await harness.rows()).toHaveLength(3);
      expect(await (await harness.search()).isFocused()).toBe(true);
    },
  );

  it('does not focus a row or summary when a reload supersedes Load more', async () => {
    const pending = new Subject<CaseListResponse>();
    list
      .mockResolvedValueOnce(CASE_LIST_FIXTURE)
      .mockReturnValueOnce(firstValueFrom(pending))
      .mockResolvedValueOnce({ items: [ownedCase], next_cursor: null });
    const { fixture, harness, store } = await render();
    await manualChangeDetection(async () => {
      await (await harness.more())?.focus();
      await (await harness.more())?.click();
      store.reload();
      pending.next({ items: [appended], next_cursor: null });
    });
    await fixture.whenStable();
    expect(await harness.rows()).toHaveLength(1);
    expect(await (await harness.loaded()).isFocused()).toBe(false);
    expect(await (await harness.rows())[0]?.isFocused()).toBe(false);
  });

  it.each(['network', 'timeout', 'unexpected'] as const)(
    'requires a case reload rather than claiming rollback after %s',
    async (kind) => {
      update.mockRejectedValueOnce(new ApiRequestError({ ...CATALOG_FAILURE, kind }));
      const { harness } = await render();
      await harness.chooseOwner(ownedCase.id, nextOwner.invited_email);
      const failure = await harness.ownerFailure();
      expect(await failure?.text()).toContain('Reload the list to see the stored owner');
      expect(await failure?.text()).not.toContain('previous owner is restored');
    },
  );

  it('reloads only the failed case when Reload to retry is activated', async () => {
    update.mockRejectedValueOnce(
      new ApiRequestError({ ...CATALOG_FAILURE, kind: 'conflict', status: 409 }),
    );
    const { harness, store } = await render();
    const reload = vi.spyOn(store, 'reloadOwner').mockResolvedValue(undefined);
    await harness.chooseOwner(ownedCase.id, nextOwner.invited_email);
    await (await harness.reloadOwner()).click();
    expect(reload).toHaveBeenCalledExactlyOnceWith(ownedCase.id);
    expect(list).toHaveBeenCalledTimes(1);
  });
});
