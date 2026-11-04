import { isSignal, provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { InviteIssuedView, MembershipView } from '@evidgate/sdk';
import type { Mock } from 'vitest';
import { MembersDataService } from '../../../core/data/members-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import type { Intent } from '../../../core/http/intent';
import { SessionStore } from '../../../core/session/session.store';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
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
const ONCE = 'recognisable-resend-one-time-value';
const ISSUED: InviteIssuedView = { ...MEMBER, invite_token: ONCE };

describe('ResendInviteDialog one-time lifecycle', () => {
  let fixture: ComponentFixture<ResendInviteDialogComponent>;
  let root: HTMLElement;
  let store: MembersStore;
  let opener: HTMLButtonElement;
  let allowed: boolean;
  let data: {
    listMemberships: Mock;
    listRoles: Mock;
    listPermissions: Mock;
    getMembership: Mock;
    resendInvite: Mock<(org: string, id: string, intent: Intent) => Promise<InviteIssuedView>>;
  };

  beforeEach(async () => {
    allowed = true;
    data = {
      listMemberships: vi.fn().mockResolvedValue({ items: [MEMBER], next_cursor: null }),
      listRoles: vi.fn().mockResolvedValue([]),
      listPermissions: vi.fn().mockResolvedValue([]),
      getMembership: vi.fn().mockResolvedValue(MEMBER),
      resendInvite: vi
        .fn<(org: string, id: string, intent: Intent) => Promise<InviteIssuedView>>()
        .mockResolvedValue(ISSUED),
    };
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        MembersStore,
        { provide: MembersDataService, useValue: data },
        { provide: SessionStore, useValue: { has: () => allowed } },
      ],
    });
    store = TestBed.inject(MembersStore);
    await store.load('org1');
    fixture = TestBed.createComponent(ResendInviteDialogComponent);
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

  it.each(['dismiss', 'escape', 'destroy'] as const)(
    'resends, reloads the row and clears every rendered value on %s',
    async (exit) => {
      press();
      await settle();
      expect(data.resendInvite).toHaveBeenCalledTimes(1);
      expect(data.getMembership).toHaveBeenCalledWith('org1', MEMBER.id);
      const control = root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]')!;
      expect(control.value).toBe(ONCE);
      expect(root.querySelector('[data-members-submit]')).toBeNull();
      const slot = fixture.debugElement.injector.get(RevealSlot);
      if (exit === 'destroy') fixture.destroy();
      else if (exit === 'escape')
        root
          .querySelector<HTMLElement>('[data-members-dialog]')!
          .dispatchEvent(
            new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
          );
      else root.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]')!.click();
      if (exit !== 'destroy') fixture.detectChanges();
      expect(slot.current()).toBeNull();
      expect(control.value).toBe('');
      expect(root.outerHTML).not.toContain(ONCE);
      for (const value of Object.values(store)) {
        if (typeof value === 'function' && isSignal(value))
          expect(JSON.stringify(value())).not.toContain(ONCE);
      }
    },
  );
  it('shows replay guidance with Close, without an empty reveal', async () => {
    data.resendInvite.mockResolvedValue({ ...MEMBER, invite_token: null });
    press();
    await settle();
    expect(root.querySelector('app-connect-step-reveal')).toBeNull();
    expect(root.querySelector('[data-members-invite-replay]')?.textContent).toContain('Use Resend');
    root.querySelector<HTMLButtonElement>('footer button')!.click();
    fixture.detectChanges();
    expect(root.querySelector('[data-members-dialog][data-state="open"]') !== null).toBe(false);
  });
  it('treats an EMPTY served token as the same replay, never as an empty reveal', async () => {
    // F2: the store normalises `''` to `null`, so the dialog must take the replay branch.
    data.resendInvite.mockResolvedValue({ ...MEMBER, invite_token: '' });
    press();
    await settle();
    expect(root.querySelector('app-connect-step-reveal')).toBeNull();
    expect(root.querySelector('[data-testid="reveal-value"]')).toBeNull();
    expect(root.querySelector('[data-members-invite-replay]')?.textContent).toContain('Use Resend');
  });
  it('renders 409 invite_not_pending verbatim as text', async () => {
    data.resendInvite.mockRejectedValue(
      new ApiRequestError({
        kind: 'conflict',
        status: 409,
        code: 'invite_not_pending',
        message: 'No longer <b>pending</b>.',
        requestId: 'req',
        retryable: false,
        details: [],
        retryAfterSeconds: null,
      }),
    );
    press();
    await settle();
    expect(root.querySelector('[data-members-error] p')?.textContent).toBe(
      'No longer <b>pending</b>.',
    );
    expect(root.querySelector('[data-members-error-code]')?.textContent).toBe('invite_not_pending');
    expect(root.querySelector('[data-members-error] b')).toBeNull();
    expect(root.querySelector('[data-members-dialog][data-state="open"]') !== null).toBe(true);
    // L5: that code means the pending row was stale, so the store re-read the list once.
    expect(data.listMemberships).toHaveBeenCalledTimes(2);
  });
  it('double clicks send one request, including while the member refresh is pending', async () => {
    // The web project's ES2022 lib does not expose Promise.withResolvers.
    let releaseResponse!: (value: InviteIssuedView) => void;
    let releaseRefresh!: (value: MembershipView) => void;
    data.resendInvite.mockReturnValue(
      new Promise<InviteIssuedView>((resolve) => {
        releaseResponse = resolve;
      }),
    );
    data.getMembership.mockReturnValue(
      new Promise<MembershipView>((resolve) => {
        releaseRefresh = resolve;
      }),
    );
    press();
    press();
    expect(data.resendInvite).toHaveBeenCalledTimes(1);
    releaseResponse(ISSUED);
    await Promise.resolve();
    await Promise.resolve();
    press();
    expect(data.resendInvite).toHaveBeenCalledTimes(1);
    releaseRefresh(MEMBER);
    await settle();
  });
  it('retries with the same intent; reopening creates a different intent', async () => {
    data.resendInvite.mockRejectedValueOnce(
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
    press();
    await settle();
    press();
    await settle();
    expect(data.resendInvite.mock.calls[1]?.[2]).toBe(data.resendInvite.mock.calls[0]?.[2]);
    fixture.componentInstance.close();
    fixture.detectChanges();
    fixture.componentInstance.open(MEMBER, opener);
    fixture.detectChanges();
    await fixture.whenStable();
    press();
    await settle();
    expect(data.resendInvite.mock.calls[2]?.[2]).not.toBe(data.resendInvite.mock.calls[0]?.[2]);
  });
  it('a late response never reopens a dismissed dialog or populates the slot', async () => {
    let release!: (value: InviteIssuedView) => void;
    data.resendInvite.mockReturnValue(
      new Promise<InviteIssuedView>((resolve) => {
        release = resolve;
      }),
    );
    press();
    fixture.componentInstance.close();
    release(ISSUED);
    await settle();
    expect(fixture.debugElement.injector.get(RevealSlot).current()).toBeNull();
    expect(root.querySelector('[data-members-dialog][data-state="open"]') !== null).toBe(false);
  });
  it('refuses non-pending rows and unauthorized opens', async () => {
    fixture.componentInstance.close();
    fixture.detectChanges();
    await fixture.whenStable();
    allowed = false;
    fixture.componentInstance.open(MEMBER, opener);
    expect(root.querySelector('[data-members-dialog][data-state="open"]') !== null).toBe(false);
    allowed = true;
    fixture.componentInstance.open({ ...MEMBER, status: 'active' }, opener);
    expect(root.querySelector('[data-members-dialog][data-state="open"]') !== null).toBe(false);
  });
});
