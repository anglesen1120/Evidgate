// QA2 independent gate (09.7.1-T3 F12): the invite token is the one plaintext the Members tab
// hands a person, and it arrives in a server response body. Two properties the suite's own specs
// do not exercise: a token carrying markup stays an inert literal end to end (wire -> store ->
// reveal slot -> DOM), and a rejected clipboard still leaves the token readable by hand.
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { InviteIssuedView, MembershipView } from '@evidgate/sdk';
import { MembersDataService } from '../../../core/data/members-data.service';
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
/** A token shaped like the server's, plus markup and quotes a hostile or buggy server could send. */
const MARKUP_TOKEN = `evgi_<img src=x onerror="globalThis.__qa2Pwned=1">_"'>${'k'.repeat(40)}`;
const showModal = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'showModal');
const close = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'close');

describe('Invite token as an inert literal (09.7.1-T3 F12 QA2)', () => {
  let fixture: ComponentFixture<ResendInviteDialogComponent>;
  let root: HTMLElement;
  let opener: HTMLButtonElement;
  let token: string | null;

  beforeEach(async () => {
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = true;
      },
    });
    Object.defineProperty(HTMLDialogElement.prototype, 'close', {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = false;
      },
    });
    token = MARKUP_TOKEN;
    const data = {
      listMemberships: vi.fn().mockResolvedValue({ items: [MEMBER], next_cursor: null }),
      listRoles: vi.fn().mockResolvedValue([]),
      listPermissions: vi.fn().mockResolvedValue([]),
      getMembership: vi.fn().mockResolvedValue(MEMBER),
      resendInvite: vi
        .fn()
        .mockImplementation(() =>
          Promise.resolve<InviteIssuedView>({ ...MEMBER, invite_token: token }),
        ),
    };
    TestBed.configureTestingModule({
      providers: [
        MembersStore,
        { provide: MembersDataService, useValue: data },
        { provide: SessionStore, useValue: { has: () => true } },
      ],
    });
    await TestBed.inject(MembersStore).load('org1');
    fixture = TestBed.createComponent(ResendInviteDialogComponent);
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
    opener = document.createElement('button');
    document.body.append(opener);
    fixture.componentInstance.open(MEMBER, opener);
    fixture.detectChanges();
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    opener.remove();
    Reflect.deleteProperty(globalThis, '__qa2Pwned');
    if (showModal) Object.defineProperty(HTMLDialogElement.prototype, 'showModal', showModal);
    else Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal');
    if (close) Object.defineProperty(HTMLDialogElement.prototype, 'close', close);
    else Reflect.deleteProperty(HTMLDialogElement.prototype, 'close');
    vi.restoreAllMocks();
  });

  async function reveal(): Promise<HTMLInputElement> {
    root.querySelector<HTMLButtonElement>('[data-members-submit]')?.click();
    await fixture.whenStable();
    fixture.detectChanges();
    const control = root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]');
    if (!control) throw new Error('Expected the reveal input');
    return control;
  }

  it('renders a server token carrying markup and quotes as the exact literal, creating no element', async () => {
    const control = await reveal();

    expect(control.value).toBe(MARKUP_TOKEN);
    // Nothing the token contains became structure: no injected element, no attribute, no handler.
    expect(root.querySelector('img')).toBeNull();
    expect(root.outerHTML).not.toContain('<img');
    expect(Object.hasOwn(globalThis, '__qa2Pwned')).toBe(false);
    // The literal lives in the control's value only, never in text or any other attribute.
    expect(root.textContent ?? '').not.toContain(MARKUP_TOKEN);
    const withoutControl = root.cloneNode(true) as HTMLElement;
    withoutControl.querySelector('[data-testid="reveal-value"]')?.remove();
    expect(withoutControl.outerHTML).not.toContain('evgi_');

    // The store published the row and nothing else.
    const store = TestBed.inject(MembersStore);
    expect(JSON.stringify(store.members())).not.toContain('evgi_');
    expect(fixture.debugElement.injector.get(RevealSlot).current()?.value).toBe(MARKUP_TOKEN);
  });

  it('keeps the token readable with manual-copy guidance when the clipboard rejects', async () => {
    const original = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    const writeText = vi.fn().mockRejectedValue(new Error('clipboard denied'));
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    try {
      const control = await reveal();
      const status = root.querySelector<HTMLElement>('[data-testid="reveal-copy-status"]');
      if (!status) throw new Error('Expected the copy status region');
      // The static warning is always on screen; the status region is what the click fills, so an
      // empty one before the click is what makes the assertion after it mean something.
      expect(status.textContent?.trim()).toBe('');

      root.querySelector<HTMLButtonElement>('[data-testid="reveal-copy"]')?.click();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(writeText).toHaveBeenCalledExactlyOnceWith(MARKUP_TOKEN);
      expect(status.textContent?.trim()).toBe(
        'Shown once. Copy this token now and share it securely with the invitee, who needs it to accept. If Copy fails, select and copy the value manually.',
      );
      // The value survives, focused and selected, so the manual copy the guidance names works.
      expect(control.value).toBe(MARKUP_TOKEN);
      expect(document.activeElement).toBe(control);
    } finally {
      if (original) Object.defineProperty(navigator, 'clipboard', original);
      else Reflect.deleteProperty(navigator, 'clipboard');
    }
  });

  it('shows the resend hint instead of a reveal when a replayed answer carries no token', async () => {
    token = null;
    root.querySelector<HTMLButtonElement>('[data-members-submit]')?.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(root.querySelector('app-connect-step-reveal')).toBeNull();
    expect(root.querySelector('[data-testid="reveal-value"]')).toBeNull();
    expect(root.querySelector('[data-members-invite-replay]')?.textContent).toContain('Use Resend');
    expect(fixture.debugElement.injector.get(RevealSlot).current()).toBeNull();
  });
});
