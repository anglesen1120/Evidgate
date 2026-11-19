import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { CaseDto, MembershipView } from '@evidgate/sdk';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { CASE_LIST_FIXTURE } from './case-list-store.fixtures';
import { CaseListTableComponent } from './case-list-table.component';

class CatalogTableHarness extends ComponentHarness {
  static hostSelector = 'app-case-list-table';
  readonly rows = this.locatorForAll('[data-row-id]');
  readonly viewport = this.locatorForOptional('cdk-virtual-scroll-viewport');
  readonly picker = this.locatorFor('hlm-select-trigger button');
  readonly fallback = this.locatorFor('[data-catalog-owner] > app-data-table-value');
  readonly copyStable = this.locatorFor(
    '[data-row-id] app-copy-value-button button[aria-label="Copy Stable identity"]',
  );
  readonly copyCase = this.locatorFor(
    '[data-row-id] app-copy-value-button button[aria-label="Copy Case ID"]',
  );
}

const firstCase = CASE_LIST_FIXTURE.items[0];
if (!firstCase?.owner_membership_id) throw new Error('No owned catalog fixture');
const ownedCase = firstCase;
const ownerId = firstCase.owner_membership_id;

function caseRows(count: number): CaseDto[] {
  return Array.from({ length: count }, (_, index) => ({
    ...ownedCase,
    id: `case-${index}`,
    annotation_id: `identity-${index}`,
  }));
}

describe('Catalog table readable owners and stable row identities', () => {
  provideLayoutTestStubs();
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [CaseListTableComponent],
      providers: [provideZonelessChangeDetection()],
    });
  });
  afterEach(() => vi.restoreAllMocks());

  async function render(
    rows: readonly CaseDto[] = [ownedCase],
    members = new Map<string, MembershipView>(),
  ) {
    const fixture = TestBed.createComponent(CaseListTableComponent);
    fixture.componentRef.setInput('rows', rows);
    fixture.componentRef.setInput('members', members);
    fixture.componentRef.setInput('membersReady', true);
    fixture.componentRef.setInput('canAssign', true);
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, CatalogTableHarness);
    return { fixture, harness };
  }

  it('keeps a known inactive owner readable outside the disabled no-choices picker', async () => {
    const inactive: MembershipView = {
      id: ownerId,
      invited_email: 'inactive.owner@example.test',
      org_id: 'org',
      status: 'revoked',
      roles: [],
      created_at: '2026-01-01T00:00:00Z',
      updated_at: '2026-01-01T00:00:00Z',
      version: 1,
    };
    const { harness } = await render([ownedCase], new Map([[inactive.id, inactive]]));
    expect(await (await harness.picker()).getProperty('disabled')).toBe(true);
    expect(await (await harness.fallback()).text()).toContain(inactive.invited_email);
  });

  it('discards a focus request for a row removed before rendering', async () => {
    const rows = caseRows(2);
    const target = rows[1];
    if (!target) throw new Error('Missing second row');
    const { fixture, harness } = await render(rows);
    fixture.componentInstance.focusRow(target.id);
    fixture.componentRef.setInput('rows', rows.slice(0, 1));
    await fixture.whenStable();
    fixture.componentRef.setInput('rows', rows);
    await fixture.whenStable();
    expect(await (await harness.rows())[1]?.isFocused()).toBe(false);
  });

  it('does not retain a focus request for an already absent row', async () => {
    const { fixture, harness } = await render([]);
    fixture.componentInstance.focusRow(ownedCase.id);
    await fixture.whenStable();
    fixture.componentRef.setInput('rows', [ownedCase]);
    await fixture.whenStable();
    expect(await (await harness.rows())[0]?.isFocused()).toBe(false);
  });

  it('keeps separately labelled stable identity and case copy actions at the 200 to 201 boundary', async () => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1280);
    const copy = vi.fn().mockResolvedValue(undefined);
    const clipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText: copy },
    });
    try {
      const { fixture, harness } = await render(caseRows(200));
      expect(await harness.viewport()).toBeNull();
      expect(await harness.rows()).toHaveLength(200);
      fixture.componentRef.setInput('rows', caseRows(201));
      await fixture.whenStable();
      expect(await harness.viewport()).not.toBeNull();
      expect((await harness.rows()).length).toBeLessThan(201);
      expect(await (await harness.rows())[0]?.getCssValue('height')).toBe('44px');
      await (await harness.copyStable()).click();
      expect(copy).toHaveBeenLastCalledWith('identity-0');
      await (await harness.copyCase()).click();
      expect(copy).toHaveBeenLastCalledWith('case-0');
      expect(await (await harness.copyStable()).getAttribute('data-copy-size')).toBe('lg');
      expect(await (await harness.copyCase()).getAttribute('data-copy-size')).toBe('lg');
    } finally {
      if (clipboard) Object.defineProperty(navigator, 'clipboard', clipboard);
      else Reflect.deleteProperty(navigator, 'clipboard');
    }
  }, 20_000);
});
