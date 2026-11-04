// QA2 independent gate (F12 "server errors verbatim (409 invite_not_pending, 429)"): the store
// proves a 429 reaches its state; nothing proved the resend and cancel dialogs render the server's
// countdown and hold the submit until it elapses. Real store, fake data service.
import { TestBed } from '@angular/core/testing';
import type { MembershipView } from '@evidgate/sdk';
import { MembersDataService } from '../../../core/data/members-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { CancelInviteDialogComponent } from './cancel-invite-dialog.component';
import { MembersStore } from './members.store';
import { ResendInviteDialogComponent } from './resend-invite-dialog.component';

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
const THROTTLED = new ApiRequestError({
  kind: 'rate_limited',
  status: 429,
  code: 'rate_limited',
  message: 'Invite burst limit reached. Retry after the window.',
  requestId: 'request-throttled',
  retryable: true,
  retryAfterSeconds: 30,
  details: [],
});

describe('Invite dialogs under a 429 (F12 QA2)', () => {
  let opener: HTMLButtonElement;
  const memberships = vi.fn();
  const roles = vi.fn();
  const permissions = vi.fn();
  const membership = vi.fn();
  const resendInvite = vi.fn();
  const cancelInvite = vi.fn();

  beforeEach(async () => {
    memberships.mockResolvedValue({ items: [MEMBER], next_cursor: null });
    roles.mockResolvedValue([]);
    permissions.mockResolvedValue([]);
    membership.mockResolvedValue(MEMBER);
    resendInvite.mockRejectedValue(THROTTLED);
    cancelInvite.mockRejectedValue(THROTTLED);
    TestBed.configureTestingModule({
      providers: [
        MembersStore,
        {
          provide: MembersDataService,
          useValue: {
            listMemberships: memberships,
            listRoles: roles,
            listPermissions: permissions,
            getMembership: membership,
            resendInvite,
            cancelInvite,
          },
        },
        { provide: SessionStore, useValue: { has: () => true } },
      ],
    });
    await TestBed.inject(MembersStore).load('org1');
    opener = document.createElement('button');
    document.body.append(opener);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    opener.remove();
    vi.clearAllMocks();
    vi.restoreAllMocks();
  });

  /** The server's own words, its code, its countdown, and a submit the countdown keeps closed. */
  function expectThrottled(root: HTMLElement): HTMLButtonElement {
    expect(root.querySelector('[data-members-error] p')?.textContent).toBe(THROTTLED.message);
    expect(root.querySelector('[data-members-error-code]')?.textContent).toBe('rate_limited');
    expect(root.querySelector('app-retry-after')).not.toBeNull();
    const submit = root.querySelector<HTMLButtonElement>('[data-members-submit]');
    if (!submit) throw new Error('Expected the submit button');
    expect(submit.disabled).toBe(true);
    return submit;
  }

  it('resend renders the throttled refusal verbatim and sends nothing until the countdown ends', async () => {
    const fixture = TestBed.createComponent(ResendInviteDialogComponent);
    fixture.detectChanges();
    const root = document.body;
    fixture.componentInstance.open(MEMBER, opener);
    fixture.detectChanges();
    await fixture.whenStable();

    root.querySelector<HTMLButtonElement>('[data-members-submit]')?.click();
    await fixture.whenStable();
    fixture.detectChanges();

    const submit = expectThrottled(root);
    submit.click();
    expect(resendInvite).toHaveBeenCalledTimes(1);
    expect(TestBed.inject(MembersStore).resendState().error?.retryAfterSeconds).toBe(30);
    // The dialog is not left in a stuck submitting state.
    expect(TestBed.inject(MembersStore).resendState().phase).toBe('failed');
  });

  it('cancel renders the throttled refusal verbatim and keeps the dialog open', async () => {
    const fixture = TestBed.createComponent(CancelInviteDialogComponent);
    fixture.detectChanges();
    const root = document.body;
    fixture.componentInstance.open(MEMBER, opener);
    fixture.detectChanges();
    await fixture.whenStable();

    root.querySelector<HTMLButtonElement>('[data-members-submit]')?.click();
    await fixture.whenStable();
    fixture.detectChanges();

    const submit = expectThrottled(root);
    submit.click();
    expect(cancelInvite).toHaveBeenCalledTimes(1);
    expect(root.querySelector('[data-members-dialog][data-state="open"]') !== null).toBe(true);
    expect(TestBed.inject(MembersStore).cancelState().phase).toBe('failed');
  });
});
