// QA (09.7.1-T2 independent gate): the remove dialog driven through the REAL MembersStore, with only
// MembersDataService and SessionStore replaced. No existing spec covers this dialog directly; the
// page spec replaces the store.

import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { MembershipView } from '@evidgate/sdk';
import { MembersDataService } from '../../../core/data/members-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../../core/http/api-error';
import type { Intent } from '../../../core/http/intent';
import { SessionStore } from '../../../core/session/session.store';
import { RemoveDialogComponent } from './remove-dialog.component';
import { MembersStore } from './members.store';
import { MEMBERS_DESKTOP } from './members-page.test-fixtures';

function member(id: string, email: string): MembershipView {
  return {
    id,
    org_id: 'org1',
    invited_email: email,
    status: 'active',
    roles: [],
    created_at: '2026-10-25T00:00:00Z',
    updated_at: '2026-10-25T00:00:00Z',
    version: 1,
  };
}

function refusal(
  kind: ApiErrorKind,
  status: number,
  code: string,
  message: string,
  retryable: boolean,
): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code,
    message,
    requestId: 'req-qa',
    retryable,
    details: [],
    retryAfterSeconds: null,
  });
}

/** Records every removal; `outcomes` answers the next removal (default: success). */
class FakeData {
  readonly granted = signal(true);
  readonly removals: Array<{ membershipId: string; intent: Intent }> = [];
  readonly outcomes: Array<() => Promise<void>> = [];
  listMemberships() {
    return Promise.resolve({ items: [], next_cursor: null });
  }
  listRoles() {
    return Promise.resolve([]);
  }
  listPermissions() {
    return Promise.resolve([]);
  }
  invite(): never {
    throw new Error('invite is not used by the remove dialog');
  }
  remove(_orgId: string, membershipId: string, intent: Intent): Promise<void> {
    this.removals.push({ membershipId, intent });
    const next = this.outcomes.shift();
    return next ? next() : Promise.resolve();
  }
}

const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

describe('RemoveDialog through the real MembersStore (09.7.1-T2 QA)', () => {
  let data: FakeData;
  let store: MembersStore;
  let fixture: ComponentFixture<RemoveDialogComponent>;
  let root: HTMLElement;
  let opener: HTMLButtonElement;
  let completed: number;
  const ALICE = member('m-alice', 'alice@example.test');
  const BOB = member('m-bob', 'bob@example.test');

  beforeEach(async () => {
    data = new FakeData();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        MEMBERS_DESKTOP,
        MembersStore,
        { provide: MembersDataService, useValue: data },
        {
          provide: SessionStore,
          useValue: { has: (code: string) => code === 'membership:write' && data.granted() },
        },
      ],
    });
    store = TestBed.inject(MembersStore);
    await store.load('org1');
    fixture = TestBed.createComponent(RemoveDialogComponent);
    fixture.detectChanges();
    root = document.body;
    opener = document.createElement('button');
    document.body.append(opener);
    completed = 0;
    fixture.componentInstance.completed.subscribe(() => completed++);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    opener.remove();
    vi.restoreAllMocks();
  });

  function submitButton(): HTMLButtonElement {
    const value = root.querySelector<HTMLButtonElement>('[data-members-submit]');
    if (!value) throw new Error('submit button missing');
    return value;
  }
  async function open(target: MembershipView): Promise<void> {
    fixture.componentInstance.open(target, opener);
    fixture.detectChanges();
    await fixture.whenStable();
  }
  function dialogOpen(): boolean {
    return root.querySelector('[data-members-dialog][data-state="open"]') !== null;
  }

  it('a double click sends one removal and announces one completion', async () => {
    let release!: () => void;
    data.outcomes.push(() => new Promise<void>((resolve) => (release = resolve)));
    await open(ALICE);
    submitButton().click();
    submitButton().click();
    await flush();
    expect(data.removals).toHaveLength(1);
    release();
    await flush();
    await fixture.whenStable();
    expect(data.removals).toHaveLength(1);
    expect(completed).toBe(1);
    expect(dialogOpen()).toBe(false);
  });

  it('a retry after a 503 unavailable replays the same membership and the same Idempotency-Key', async () => {
    data.outcomes.push(() =>
      Promise.reject(refusal('unavailable', 503, 'unavailable', 'Try again shortly.', true)),
    );
    await open(ALICE);
    submitButton().click();
    await flush();
    fixture.detectChanges();
    expect(root.querySelector('[data-members-error-code]')?.textContent?.trim()).toBe(
      'unavailable',
    );
    submitButton().click();
    await flush();
    expect(data.removals).toHaveLength(2);
    expect(data.removals[1]?.membershipId).toBe('m-alice');
    expect(data.removals[1]?.intent).toBe(data.removals[0]?.intent);
    expect(completed).toBe(1);
  });

  it('a failed removal of Alice does not hand her Idempotency-Key to Bob', async () => {
    data.outcomes.push(() =>
      Promise.reject(refusal('unavailable', 503, 'unavailable', 'Try again shortly.', true)),
    );
    await open(ALICE);
    submitButton().click();
    await flush();
    await open(BOB);
    submitButton().click();
    await flush();
    expect(data.removals.map((r) => r.membershipId)).toEqual(['m-alice', 'm-bob']);
    expect(data.removals[1]?.intent).not.toBe(data.removals[0]?.intent);
  });

  it('closing after a failure and reopening the same member starts a new Idempotency-Key', async () => {
    data.outcomes.push(() =>
      Promise.reject(refusal('unavailable', 503, 'unavailable', 'Try again shortly.', true)),
    );
    await open(ALICE);
    submitButton().click();
    await flush();
    fixture.componentInstance.close();
    await open(ALICE);
    submitButton().click();
    await flush();
    expect(data.removals).toHaveLength(2);
    expect(data.removals[1]?.intent).not.toBe(data.removals[0]?.intent);
  });

  it('a removal is refused while another removal is in flight: open() does not switch the member', async () => {
    let release!: () => void;
    data.outcomes.push(() => new Promise<void>((resolve) => (release = resolve)));
    await open(ALICE);
    submitButton().click();
    await flush();
    await open(BOB);
    fixture.detectChanges();
    expect(root.textContent).toContain('alice@example.test');
    expect(root.textContent).not.toContain('bob@example.test');
    release();
    await flush();
  });

  it('a last_admin refusal shows the server code and keeps the dialog open with no completion', async () => {
    data.outcomes.push(() =>
      Promise.reject(
        refusal('conflict', 409, 'last_admin', 'The last administrator cannot be removed.', false),
      ),
    );
    await open(ALICE);
    submitButton().click();
    await flush();
    fixture.detectChanges();
    expect(root.querySelector('[data-members-error-code]')?.textContent?.trim()).toBe('last_admin');
    expect(dialogOpen()).toBe(true);
    expect(completed).toBe(0);
    await fixture.whenStable();
    const summary = root.querySelector<HTMLElement>('[data-members-error]')!;
    expect(document.activeElement).toBe(summary);
    expect(summary.getAttribute('role')).toBe('group');
    expect(summary.getAttribute('aria-describedby')).toBe(
      'members-remove-error-message members-remove-error-code',
    );
    expect(root.querySelector('[role="alert"]')).toBeNull();
    summary.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
    );
    await fixture.whenStable();
    expect(dialogOpen()).toBe(false);
    expect(document.activeElement).toBe(opener);
    expect(root.querySelector('[data-members-error]')).toBeNull();
  });

  it('the server message is rendered as text, never as markup', async () => {
    data.outcomes.push(() =>
      Promise.reject(
        refusal('conflict', 409, 'invalid_transition', 'Status <img src=x> blocks removal.', false),
      ),
    );
    await open(ALICE);
    submitButton().click();
    await flush();
    fixture.detectChanges();
    expect(root.querySelector('[data-members-error] p')?.textContent).toBe(
      'Status <img src=x> blocks removal.',
    );
    expect(root.querySelector('[data-members-error] img')).toBeNull();
  });

  it('revoking membership:write while the dialog is open blocks the submit', async () => {
    await open(ALICE);
    data.granted.set(false);
    fixture.detectChanges();
    expect(submitButton().disabled).toBe(true);
    submitButton().click();
    await flush();
    expect(data.removals).toHaveLength(0);
  });

  it('open() without membership:write leaves the dialog closed', async () => {
    data.granted.set(false);
    await open(ALICE);
    expect(dialogOpen()).toBe(false);
    expect(root.textContent).not.toContain('alice@example.test');
  });

  // A success is a fact about the list, not about this dialog (remove-dialog.component.ts:143-150):
  // close() still bumps the generation mid-flight, but that now only skips the dialog's own close,
  // never the completion emit, so the page confirms the removal.
  it('closing while a removal is in flight still announces its completion', async () => {
    let release!: () => void;
    data.outcomes.push(() => new Promise<void>((resolve) => (release = resolve)));
    await open(ALICE);
    submitButton().click();
    fixture.componentInstance.close();
    release();
    await flush();
    expect(completed).toBe(1);
  });
});
