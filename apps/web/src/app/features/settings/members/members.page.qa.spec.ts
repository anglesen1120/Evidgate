import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { MembershipListResponse, MembershipView } from '@evidgate/sdk';
import { MembersDataService } from '../../../core/data/members-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { MembersPage } from './members.page';
import { MembersStore } from './members.store';
import { pageMember, ROLES } from './members.store.test-support';

const MEMBER = pageMember(0);
const SECOND: MembershipView = { ...MEMBER, id: 'second', invited_email: 'second@example.test' };
const REFUSAL = 'The membership list is unavailable right now.';

class PagedData {
  next: () => Promise<MembershipListResponse> = () =>
    Promise.resolve({ items: [SECOND], next_cursor: null });
  readonly limits: number[] = [];
  listMemberships(_org: string, cursor?: string, limit = 20): Promise<MembershipListResponse> {
    this.limits.push(limit);
    return cursor === undefined
      ? Promise.resolve({ items: [MEMBER], next_cursor: 'next' })
      : this.next();
  }
  listRoles() { return Promise.resolve(ROLES); }
  listPermissions() { return Promise.resolve([]); }
}

class MembersHarness extends ComponentHarness {
  static hostSelector = 'app-members-page';
  readonly next = this.locatorFor('app-data-table-pager [data-page="next"]');
  readonly previous = this.locatorFor('app-data-table-pager [data-page="previous"]');
  readonly summary = this.locatorFor('app-data-table-pager [role="status"]');
  readonly list = this.locatorFor('app-member-list');
  readonly alert = this.locatorForOptional('[data-members-load-error]');
  readonly alertMessage = this.locatorForOptional('[data-members-load-error] p');
  readonly alertCode = this.locatorForOptional('[data-members-load-error] code');
  readonly counts = this.locatorForAll('[data-members-page-count] app-number-value');
  readonly size = this.locatorFor('app-data-table-pager hlm-select-trigger button');
  readonly view = this.locatorFor('app-member-list [data-member-row-view]');
}

class OptionHarness extends ComponentHarness {
  static hostSelector = '[role="option"]';
}

class DialogHarness extends ComponentHarness {
  static hostSelector = '[role="dialog"]';
}

describe('MembersPage cursor pagination', () => {
  provideLayoutTestStubs();
  let data: PagedData;
  let fixture: ComponentFixture<MembersPage>;
  let store: MembersStore;

  beforeEach(() => {
    data = new PagedData();
    TestBed.configureTestingModule({
      imports: [MembersPage],
      providers: [
        provideZonelessChangeDetection(),
        { provide: MembersDataService, useValue: data },
        { provide: SessionStore, useValue: { currentOrg: signal({ id: 'org' }), has: () => false } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  async function render() {
    fixture = TestBed.createComponent(MembersPage);
    fixture.detectChanges();
    await fixture.whenStable();
    store = fixture.debugElement.injector.get(MembersStore);
    return TestbedHarnessEnvironment.harnessForFixture(fixture, MembersHarness);
  }

  function unavailable(): Promise<MembershipListResponse> {
    return Promise.reject(new ApiRequestError({
      kind: 'unavailable', status: 503, code: 'unavailable', message: REFUSAL,
      requestId: 'request', retryable: true, details: [], retryAfterSeconds: null,
    }));
  }

  it('replaces rows and page counts, keeps all role columns, and supports Previous after Next', async () => {
    const page = await render();
    expect(await (await page.summary()).text()).toBe('Page 1');
    expect(await (await page.previous()).getAttribute('aria-disabled')).toBe('true');
    await (await page.next()).click();
    expect(await (await page.summary()).text()).toBe('Page 2');
    expect(await (await page.list()).text()).toContain(SECOND.invited_email);
    expect(await (await page.list()).text()).not.toContain(MEMBER.invited_email);
    expect(await (await page.next()).getAttribute('aria-disabled')).toBe('true');
    expect(store.matrix().columns).toEqual(ROLES);
    expect(store.members()).toEqual([SECOND]);
    const counts = await page.counts();
    expect(await counts[0]?.text()).toBe('1');
    await (await page.previous()).click();
    expect(await (await page.summary()).text()).toBe('Page 1');
    expect(store.members()).toEqual([MEMBER]);
  });

  it('retains the prior page and announces a transient failure until Next retries successfully', async () => {
    data.next = unavailable;
    const page = await render();
    await (await page.next()).click();
    expect(await (await page.summary()).text()).toBe('Page 1');
    expect(await (await page.alert())?.getAttribute('role')).toBe('alert');
    expect(await (await page.alertMessage())?.text()).toBe(REFUSAL);
    expect(await (await page.alertCode())?.text()).toBe('unavailable');
    expect(await (await page.list()).text()).toContain(MEMBER.invited_email);
    expect(await (await page.next()).getAttribute('aria-disabled')).toBe('false');
    data.next = () => Promise.resolve({ items: [SECOND], next_cursor: null });
    await (await page.next()).click();
    expect(await page.alert()).toBeNull();
    expect(await (await page.summary()).text()).toBe('Page 2');
    expect(await (await page.list()).text()).toContain(SECOND.invited_email);
  });

  it('handles a contract violation generically without leaking thrown text or stealing focus', async () => {
    data.next = () => Promise.reject(new Error('private malformed envelope'));
    const page = await render();
    const next = await page.next();
    await next.focus();
    await next.click();
    expect(await (await page.alert())?.text()).toContain('The members page could not be loaded.');
    expect(await (await page.alert())?.text()).not.toContain('private malformed envelope');
    expect(await page.alertCode()).toBeNull();
    expect(await next.isFocused()).toBe(true);
    expect(store.phase()).toBe('ready');
    expect(store.members()).toEqual([MEMBER]);
  });

  it('resets page one and sends the selected size through the shared selector', async () => {
    const page = await render();
    await (await page.next()).click();
    await (await page.size()).click();
    const options = await TestbedHarnessEnvironment.documentRootLoader(fixture).getAllHarnesses(OptionHarness);
    const texts = await Promise.all(options.map(async (option) => (await option.host()).text()));
    expect(texts).toEqual(['20', '50', '100']);
    const fifty = options[texts.indexOf('50')];
    if (fifty === undefined) throw new Error('50-row option missing');
    await (await fifty.host()).click();
    expect(await (await page.summary()).text()).toBe('Page 1');
    expect(data.limits).toEqual([20, 20, 50]);
  });

  it('keeps page two when View is opened and dismissed', async () => {
    const page = await render();
    await (await page.next()).click();
    await (await page.view()).click();
    const overlay = TestbedHarnessEnvironment.documentRootLoader(fixture);
    const dialog = await overlay.getHarness(DialogHarness);
    expect(await (await dialog.host()).text()).toContain(SECOND.invited_email);
    await (await dialog.host()).sendKeys(TestKey.ESCAPE);
    expect(await overlay.hasHarness(DialogHarness)).toBe(false);
    expect(await (await page.summary()).text()).toBe('Page 2');
    expect(data.limits).toEqual([20, 20]);
  });
});
