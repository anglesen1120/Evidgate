import { Location } from '@angular/common';
import { SpyLocation } from '@angular/common/testing';
import { TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import type { MembershipView } from '@evidgate/sdk';
import { BehaviorSubject } from 'rxjs';
import { CaseListDataService } from '../../core/data/case-list-data.service';
import { SessionStore } from '../../core/session/session.store';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import { CatalogMembersReader } from './catalog-members.reader';
import { CatalogPageHarness } from './case-list-page.testing';
import { CASE_LIST_FIXTURE } from './case-list-store.fixtures';
import { CaseListPage } from './case-list.page';

const row = CASE_LIST_FIXTURE.items[0];
const other = CASE_LIST_FIXTURE.items[1];
if (!row?.owner_membership_id || !other) throw new Error('Catalog fixtures need two owned rows');
const first = row;
const second = other;
const owner: MembershipView = {
  id: row.owner_membership_id,
  invited_email: 'owner@example.test',
  org_id: 'org',
  status: 'active',
  roles: [],
  created_at: '2026-01-01T00:00:00Z',
  updated_at: '2026-01-01T00:00:00Z',
  version: 1,
};
const next = { ...owner, id: 'next-owner', invited_email: 'next@example.test' };

class RecordHarness extends CatalogPageHarness {
  readonly close = this.documentRootLocatorFactory().locatorFor('button[aria-label="Close"]');
  readonly backdrop = this.documentRootLocatorFactory().locatorFor(
    '.cdk-overlay-backdrop.spartan-dialog-overlay',
  );
  readonly buttons = this.documentRootLocatorFactory().locatorForAll('[role="dialog"] button');
  readonly owner = this.documentRootLocatorFactory().locatorFor('#catalog-record-owner');
}

describe('Catalog record dismissal and read-only boundaries', () => {
  provideLayoutTestStubs();
  const width = signal(1440);
  const permission = signal(true);
  const list = vi.fn<CaseListDataService['list']>();
  const update = vi.fn<CaseListDataService['update']>();
  const memberInit = vi.fn();
  const members = signal<ReadonlyMap<string, MembershipView>>(new Map());

  beforeEach(() => {
    width.set(1440);
    permission.set(true);
    list.mockReset().mockResolvedValue(CASE_LIST_FIXTURE);
    update.mockReset();
    memberInit.mockReset();
    members.set(
      new Map([
        [owner.id, owner],
        [next.id, next],
      ]),
    );
    const params = new BehaviorSubject(convertToParamMap({ orgId: 'org', projectId: 'project' }));
    TestBed.configureTestingModule({
      imports: [CaseListPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '**', children: [] }]),
        { provide: CaseListDataService, useValue: { list, update } },
        { provide: SessionStore, useValue: { has: () => permission() } },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate: () => width() >= 768,
            allowsMutation: () => width() >= 768,
          },
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
              members,
              status: () => 'ready',
              failure: () => null,
              init: memberInit,
              clear: vi.fn(),
              reload: vi.fn(),
            },
          },
        ],
      },
    });
  });

  afterEach(() => vi.restoreAllMocks());

  async function mount() {
    const fixture = TestBed.createComponent(CaseListPage);
    await fixture.whenStable();
    const ui = await TestbedHarnessEnvironment.harnessForFixture(fixture, RecordHarness);
    return { fixture, ui };
  }

  it.each(['Escape', 'Close', 'Cancel', 'backdrop', 'other record', 'navigation'] as const)(
    'guards dirty %s without writing and retains the draft after Keep editing',
    async (exit) => {
      const { ui } = await mount();
      await ui.draftOwner(first.id, next.invited_email);
      if (exit === 'Escape') await (await ui.cancel()).sendKeys(TestKey.ESCAPE);
      if (exit === 'Close') await (await ui.close()).click();
      if (exit === 'Cancel') await (await ui.cancel()).click();
      if (exit === 'backdrop') await (await ui.backdrop()).click();
      if (exit === 'other record') await (await ui.view(second.id)).click();
      if (exit === 'navigation') await TestBed.inject(Router).navigateByUrl('/elsewhere');
      expect(await (await ui.keep()).isFocused()).toBe(true);
      expect(await (await ui.dialogTitle()).text()).toContain('Discard unsaved changes?');
      await (await ui.keep()).click();
      expect(await (await ui.owner()).isFocused()).toBe(true);
      expect(await (await ui.save()).getProperty('disabled')).toBe(false);
      expect(await (await ui.owner()).text()).toContain(next.invited_email);
      expect(update).not.toHaveBeenCalled();
    },
  );

  it('guards browser Back and restores editing without writing', async () => {
    const location = new SpyLocation();
    TestBed.overrideProvider(Location, { useValue: location });
    const { ui } = await mount();
    TestBed.inject(Router).setUpLocationChangeListener();
    await ui.draftOwner(first.id, next.invited_email);
    location.simulateUrlPop('/elsewhere');
    await vi.waitFor(async () => {
      expect(await (await ui.keep()).isFocused()).toBe(true);
    });
    await (await ui.keep()).click();
    expect(await (await ui.owner()).isFocused()).toBe(true);
    expect(await (await ui.owner()).text()).toContain(next.invited_email);
    expect(update).not.toHaveBeenCalled();
  });

  it('uses the browser unload prompt instead of writing a dirty owner', async () => {
    const { ui } = await mount();
    await ui.draftOwner(first.id, next.invited_email);
    const event = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
    expect(update).not.toHaveBeenCalled();
    await (await ui.cancel()).click();
    await (await ui.discard()).click();
    const clean = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(clean);
    expect(clean.defaultPrevented).toBe(false);
  });

  it('returns to the exact opener after Escape from discard confirmation then confirmed discard', async () => {
    const { ui } = await mount();
    const opener = await ui.edit(first.id);
    if (!opener) throw new Error('Expected editable desktop row');
    await ui.draftOwner(first.id, next.invited_email);
    await (await ui.cancel()).click();
    await (await ui.keep()).sendKeys(TestKey.ESCAPE);
    expect(await (await ui.owner()).text()).toContain(next.invited_email);
    await (await ui.cancel()).click();
    await (await ui.discard()).click();
    expect(await ui.dialog()).toBeNull();
    expect(await opener.isFocused()).toBe(true);
    expect(update).not.toHaveBeenCalled();
  });

  it.each([599, 600, 767, 768])(
    'offers Edit only under the mutation policy at %ipx',
    async (pixels) => {
      width.set(pixels);
      const { ui } = await mount();
      if (pixels < 768) expect(await ui.edit(first.id)).toBeNull();
      else expect(await ui.edit(first.id)).not.toBeNull();
      await (await ui.view(first.id)).click();
      const labels = await Promise.all((await ui.buttons()).map((button) => button.text()));
      const edits = labels.filter((label) => label.trim() === 'Edit owner');
      expect(edits).toHaveLength(pixels < 768 ? 0 : 1);
      expect(update).not.toHaveBeenCalled();
    },
  );

  it('does not request data or write on View and restores its exact opener on Escape', async () => {
    const { ui } = await mount();
    expect(await ui.edit(first.id)).not.toBeNull();
    const opener = await ui.view(first.id);
    const reads = list.mock.calls.length;
    const memberReads = memberInit.mock.calls.length;
    await opener.click();
    expect(await (await ui.dialogTitle()).isFocused()).toBe(true);
    await (await ui.dialogTitle()).sendKeys(TestKey.ESCAPE);
    expect(await ui.dialog()).toBeNull();
    expect(await opener.isFocused()).toBe(true);
    expect(list).toHaveBeenCalledTimes(reads);
    expect(memberInit).toHaveBeenCalledTimes(memberReads);
    expect(update).not.toHaveBeenCalled();
  });
});
