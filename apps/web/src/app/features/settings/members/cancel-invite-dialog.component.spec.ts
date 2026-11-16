import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { MembershipView } from '@evidgate/sdk';
import type { Mock } from 'vitest';
import { MembersDataService } from '../../../core/data/members-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import type { Intent } from '../../../core/http/intent';
import { SessionStore } from '../../../core/session/session.store';
import { CancelInviteDialogComponent } from './cancel-invite-dialog.component';
import { MembersStore } from './members.store';
import { MEMBERS_DESKTOP } from './members-page.test-fixtures';

const MEMBER: MembershipView = {
  id: 'pending',
  org_id: 'org1',
  invited_email: 'new@example.test',
  status: 'pending',
  roles: [],
  created_at: '2026-10-25T00:00:00Z',
  updated_at: '2026-10-25T00:00:00Z',
  version: 1,
};

describe('CancelInviteDialog pending invitation confirmation', () => {
  let fixture: ComponentFixture<CancelInviteDialogComponent>;
  let root: HTMLElement;
  let opener: HTMLButtonElement;
  let allowed: boolean;
  let data: {
    listMemberships: Mock;
    listRoles: Mock;
    listPermissions: Mock;
    cancelInvite: Mock<(org: string, id: string, intent: Intent) => Promise<void>>;
  };
  beforeEach(async () => {
    allowed = true;
    data = {
      listMemberships: vi.fn().mockResolvedValue({ items: [MEMBER], next_cursor: null }),
      listRoles: vi.fn().mockResolvedValue([]),
      listPermissions: vi.fn().mockResolvedValue([]),
      cancelInvite: vi
        .fn<(org: string, id: string, intent: Intent) => Promise<void>>()
        .mockResolvedValue(undefined),
    };
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        MEMBERS_DESKTOP,
        MembersStore,
        { provide: MembersDataService, useValue: data },
        { provide: SessionStore, useValue: { has: () => allowed } },
      ],
    });
    await TestBed.inject(MembersStore).load('org1');
    fixture = TestBed.createComponent(CancelInviteDialogComponent);
    fixture.detectChanges();
    root = document.body;
    opener = document.createElement('button');
    document.body.append(opener);
    fixture.componentInstance.open(MEMBER, opener);
    fixture.detectChanges();
    await fixture.whenStable();
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    opener.remove();
    vi.restoreAllMocks();
  });
  function press() {
    root.querySelector<HTMLButtonElement>('[data-members-submit]')!.click();
  }
  async function settle() {
    await fixture.whenStable();
    fixture.detectChanges();
  }
  it('cancels, reloads the real store, closes and restores the opener', async () => {
    const completed = vi.fn();
    fixture.componentInstance.completed.subscribe(completed);
    press();
    await settle();
    expect(data.cancelInvite).toHaveBeenCalledWith('org1', MEMBER.id, expect.anything());
    expect(data.listMemberships).toHaveBeenCalledTimes(2);
    expect(root.querySelector('[data-members-dialog][data-state="open"]') !== null).toBe(false);
    expect(document.activeElement).toBe(opener);
    expect(completed).toHaveBeenCalledTimes(1);
  });
  it('shows 409 invite_not_pending verbatim without closing, and drops the stale row', async () => {
    data.cancelInvite.mockRejectedValue(
      new ApiRequestError({
        kind: 'conflict',
        status: 409,
        code: 'invite_not_pending',
        message: 'Invitation is <b>not pending</b>.',
        requestId: 'req',
        retryable: false,
        details: [],
        retryAfterSeconds: null,
      }),
    );
    press();
    await settle();
    expect(root.querySelector('[data-members-error] p')?.textContent).toBe(
      'Invitation is <b>not pending</b>.',
    );
    expect(root.querySelector('[data-members-error-code]')?.textContent).toBe('invite_not_pending');
    expect(root.querySelector('[data-members-error] b')).toBeNull();
    expect(root.querySelector('[data-members-dialog][data-state="open"]') !== null).toBe(true);
    // L5: the refusal means the pending row was stale, so the store re-read the list once.
    expect(data.listMemberships).toHaveBeenCalledTimes(2);
  });
  it('double click sends one cancellation and a retry reuses the original intent', async () => {
    // ES2022 test target: Promise.withResolvers is unavailable.
    let reject!: (reason: unknown) => void;
    data.cancelInvite.mockReturnValueOnce(
      new Promise<void>((_resolve, refuse) => {
        reject = refuse;
      }),
    );
    press();
    press();
    expect(data.cancelInvite).toHaveBeenCalledTimes(1);
    reject(
      new ApiRequestError({
        kind: 'unavailable',
        status: 503,
        code: 'busy',
        message: 'Try again.',
        requestId: 'req',
        retryable: true,
        details: [],
        retryAfterSeconds: null,
      }),
    );
    await settle();
    press();
    await settle();
    expect(data.cancelInvite.mock.calls[1]?.[2]).toBe(data.cancelInvite.mock.calls[0]?.[2]);
  });
  it('Escape closes without mutating; unauthorized and non-pending rows cannot open', () => {
    root
      .querySelector<HTMLElement>('[data-members-dialog]')!
      .dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
      );
    fixture.detectChanges();
    expect(root.querySelector('[data-members-dialog][data-state="open"]') !== null).toBe(false);
    expect(data.cancelInvite).not.toHaveBeenCalled();
    allowed = false;
    fixture.componentInstance.open(MEMBER, opener);
    expect(root.querySelector('[data-members-dialog][data-state="open"]') !== null).toBe(false);
    allowed = true;
    fixture.componentInstance.open({ ...MEMBER, status: 'active' }, opener);
    expect(root.querySelector('[data-members-dialog][data-state="open"]') !== null).toBe(false);
  });
});
