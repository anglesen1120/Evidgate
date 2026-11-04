// QA (09.7.1-T2 independent gate): the Members page with the REAL MembersStore; only the data
// service and session are replaced. The existing page spec replaces the store, so the paging path
// (`next()` -> `store.loadMore()`) and what the user sees after a refused page had no test.

import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { MembershipView, RoleView } from '@evidgate/sdk';
import { MembersDataService } from '../../../core/data/members-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { MembersPage } from './members.page';
import { MembersStore } from './members.store';

const ROLE: RoleView = {
  id: 'role-a',
  code: 'tenant_admin',
  name: 'Organization administrator',
  is_builtin: true,
  created_at: '2026-10-25T00:00:00Z',
  version: 1,
};
const MEMBER: MembershipView = {
  id: 'm1',
  org_id: 'org1',
  invited_email: 'first@example.test',
  status: 'active',
  roles: [],
  created_at: '2026-10-25T00:00:00Z',
  updated_at: '2026-10-25T00:00:00Z',
  version: 1,
};
const PAGE_TWO_REFUSAL = 'The membership list is unavailable right now.';

/** Page 1 has one row and a second cursor; page 2 answers whatever the test installs. */
class PagedData {
  page2: (cursor: string) => Promise<unknown> = () =>
    Promise.resolve({ items: [], next_cursor: null });
  readonly cursors: Array<string | null> = [];
  listMemberships(_orgId: string, cursor: string | null) {
    this.cursors.push(cursor);
    if (!cursor) return Promise.resolve({ items: [MEMBER], next_cursor: 'c2' });
    return this.page2(cursor);
  }
  listRoles() {
    return Promise.resolve([ROLE]);
  }
  listPermissions() {
    return Promise.resolve([]);
  }
}

/** The page's own state, read through its component injector (the store is a component provider). */
type PageSurface = {
  next(): Promise<void>;
  more(): boolean;
};

describe('MembersPage paging through the real MembersStore (09.7.1-T2 QA)', () => {
  let data: PagedData;
  let fixture: ComponentFixture<MembersPage>;
  let root: HTMLElement;
  let store: MembersStore;

  beforeEach(() => {
    data = new PagedData();
    TestBed.configureTestingModule({
      imports: [MembersPage],
      providers: [
        provideZonelessChangeDetection(),
        { provide: MembersDataService, useValue: data },
        {
          provide: SessionStore,
          useValue: {
            currentOrg: signal({ id: 'org1' }),
            has: () => true,
          },
        },
      ],
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  // Zoneless: whenStable() does not wait for the fake's promises, so drain the timer queue
  // until the store's first load has settled.
  const settle = async () => {
    for (let i = 0; i < 5; i++) {
      await new Promise<void>((resolve) => setTimeout(resolve, 0));
      fixture.detectChanges();
    }
    await fixture.whenStable();
  };

  async function render() {
    fixture = TestBed.createComponent(MembersPage);
    fixture.detectChanges();
    await settle();
    root = fixture.nativeElement as HTMLElement;
    store = fixture.debugElement.injector.get(MembersStore);
    return fixture.componentInstance as unknown as PageSurface;
  }

  it('a refused second page keeps the first page on screen and records the refusal, with Next still offered', async () => {
    data.page2 = () =>
      Promise.reject(
        new ApiRequestError({
          kind: 'unavailable',
          status: 503,
          code: 'unavailable',
          message: PAGE_TWO_REFUSAL,
          requestId: 'req-qa',
          retryable: true,
          details: [],
          retryAfterSeconds: null,
        }),
      );
    const page = await render();
    await page.next();
    await settle();

    expect(store.phase()).toBe('ready');
    expect(store.members().map((m) => m.id)).toEqual(['m1']);
    expect(store.loadError()?.message).toBe(PAGE_TWO_REFUSAL);
    expect(page.more()).toBe(true);
    expect(root.textContent).toContain(MEMBER.invited_email);
  });

  // 09.7.1-T2 fix round F1: members.store.ts records a paging refusal in `loadError` while `phase`
  // stays `ready`, and <app-request-state>'s `ready` branch renders only the content slot, so the
  // page now renders `loadError` itself as the `data-members-load-error` alert above the nav.
  it('a refused second page is announced to the user, not silently dropped', async () => {
    data.page2 = () =>
      Promise.reject(
        new ApiRequestError({
          kind: 'unavailable',
          status: 503,
          code: 'unavailable',
          message: PAGE_TWO_REFUSAL,
          requestId: 'req-qa',
          retryable: true,
          details: [],
          retryAfterSeconds: null,
        }),
      );
    const page = await render();
    await page.next();
    await settle();

    const alert = root.querySelector('[data-members-load-error]');
    expect(alert?.getAttribute('role')).toBe('alert');
    expect(alert?.textContent).toContain(PAGE_TWO_REFUSAL);
    expect(alert?.textContent).toContain('unavailable');
    expect(alert?.querySelector('p')?.textContent?.trim()).toBe(PAGE_TWO_REFUSAL);
  });

  // 09.7.1-T2 fix round F1: the store never clears `loadError`, so the page hides its own copy once
  // a page arrives; the block must not outlive the failure it describes.
  it('a refused second page stops being announced once a retry loads a page', async () => {
    data.page2 = () =>
      Promise.reject(
        new ApiRequestError({
          kind: 'unavailable',
          status: 503,
          code: 'unavailable',
          message: PAGE_TWO_REFUSAL,
          requestId: 'req-qa',
          retryable: true,
          details: [],
          retryAfterSeconds: null,
        }),
      );
    const page = await render();
    await page.next();
    await settle();
    expect(root.querySelector('[data-members-load-error]')).not.toBeNull();

    data.page2 = () =>
      Promise.resolve({
        items: [{ ...MEMBER, id: 'm2', invited_email: 'second@example.test' }],
        next_cursor: null,
      });
    await page.next();
    await settle();

    expect(root.querySelector('[data-members-load-error]')).toBeNull();
    expect(root.textContent).toContain('second@example.test');
  });

  // 09.7.1-T2 fix round F4: next() had try/finally and no catch, so a contract violation the store
  // re-throws escaped the click handler as an unhandled rejection. The page now catches it and says
  // so generically — never echoing the thrown value.
  it('a contract violation while paging is handled by the page, not left as an unhandled rejection', async () => {
    data.page2 = () => Promise.reject(new Error('the membership list envelope was malformed'));
    const page = await render();
    await expect(page.next()).resolves.toBeUndefined();
    await settle();

    const alert = root.querySelector('[data-members-load-error]');
    expect(alert?.getAttribute('role')).toBe('alert');
    expect(alert?.textContent).toContain('The next members page could not be loaded.');
    expect(root.textContent).not.toContain('envelope was malformed');
    expect(store.phase()).toBe('ready');
    expect(store.members().map((m) => m.id)).toEqual(['m1']);
  });
});
