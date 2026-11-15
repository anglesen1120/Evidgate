import { InteractivityChecker } from '@angular/cdk/a11y';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { SessionStore } from '../../../core/session/session.store';
import { MembersPage } from './members.page';
import { MembersStore } from './members.store';
import { FakeMembersStore, MEMBERS_DESKTOP } from './members-page.test-fixtures';

function required<T extends Element>(selector: string): T {
  const element = document.body.querySelector<T>(selector);
  if (!element) throw new Error(`Missing element: ${selector}`);
  return element;
}

function enterEmail(
  fixture: ComponentFixture<MembersPage>,
  email: HTMLInputElement,
  value: string,
) {
  email.value = value;
  email.dispatchEvent(new Event('input', { bubbles: true }));
  fixture.detectChanges();
}

async function submit(fixture: ComponentFixture<MembersPage>) {
  required<HTMLButtonElement>('[data-members-dialog="invite"] [data-members-submit]').click();
  await fixture.whenStable();
}

describe('Members invitation feedback and contextual loading', () => {
  let store: FakeMembersStore;

  beforeEach(() => {
    store = new FakeMembersStore();
    TestBed.configureTestingModule({
      imports: [MembersPage],
      providers: [
        provideZonelessChangeDetection(),
        MEMBERS_DESKTOP,
        {
          provide: SessionStore,
          useValue: { currentOrg: signal({ id: 'org1' }), has: () => true },
        },
      ],
    });
    TestBed.overrideComponent(MembersPage, {
      set: { providers: [{ provide: MembersStore, useValue: store }] },
    });
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  async function openInvite() {
    const fixture = TestBed.createComponent(MembersPage);
    await fixture.whenStable();
    const opener = required<HTMLButtonElement>('[data-members-invite-open]');
    opener.click();
    await fixture.whenStable();
    return {
      fixture,
      opener,
      form: required<HTMLFormElement>('[data-members-dialog="invite"] form'),
      email: required<HTMLInputElement>('#members-invite-email'),
    };
  }

  it('retains native email semantics without native validation popups or premature errors', async () => {
    const { email, form } = await openInvite();
    expect(form.noValidate).toBe(true);
    expect(email.type).toBe('email');
    expect(email.required).toBe(true);
    expect(email.maxLength).toBe(320);
    expect(email.getAttribute('aria-invalid')).toBeNull();
    expect(email.getAttribute('aria-describedby')).toBeNull();
    expect(document.querySelector('#members-invite-validation-summary')).toBeNull();
    expect(document.activeElement).toBe(email);
    expect(store.invite).not.toHaveBeenCalled();
  });

  it.each([
    { case: 'empty', value: '', correction: 'Enter an email address.' },
    { case: 'malformed', value: 'not-an-email', correction: 'Enter a valid email address.' },
    {
      case: 'overlong',
      value: `${'a'.repeat(314)}@x.test`,
      correction: 'Use an email address with 320 characters or fewer.',
    },
  ])('links and focuses the $case correction without submitting', async ({ value, correction }) => {
    const { fixture, email, form } = await openInvite();
    const nativeValidation = vi.spyOn(form, 'reportValidity');
    enterEmail(fixture, email, value);
    await submit(fixture);
    expect(store.invite).not.toHaveBeenCalled();
    expect(nativeValidation).not.toHaveBeenCalled();
    expect(email.value).toBe(value);
    expect(email.getAttribute('aria-invalid')).toBe('true');
    const errorId = email.getAttribute('aria-describedby');
    expect(errorId).toBeTruthy();
    expect(document.getElementById(errorId ?? '')?.textContent?.trim()).toBe(correction);
    const summary = required<HTMLElement>('#members-invite-validation-summary');
    expect(summary.textContent).toContain(correction);
    await vi.waitFor(() => expect(document.activeElement).toBe(summary));
    required<HTMLAnchorElement>(
      '#members-invite-validation-summary a[href="#members-invite-email"]',
    ).click();
    expect(document.activeElement).toBe(email);
  });

  it.each([
    { case: 'regular', value: 'member@example.test' },
    { case: 'maximum-length', value: `${'a'.repeat(313)}@x.test` },
  ])('submits a $case native-valid email within the 320-character boundary', async ({ value }) => {
    const { fixture, email } = await openInvite();
    enterEmail(fixture, email, value);
    expect(email.validity.typeMismatch).toBe(false);
    await submit(fixture);
    expect(store.invite).toHaveBeenCalledTimes(1);
    expect(store.invite.mock.calls[0]?.slice(0, 2)).toEqual([value, []]);
    expect(document.querySelector('#members-invite-validation-summary')).toBeNull();
  });

  it('reveals a touched correction on blur and clears it while typing without moving focus', async () => {
    const { fixture, email } = await openInvite();
    enterEmail(fixture, email, 'not-an-email');
    expect(email.getAttribute('aria-invalid')).toBeNull();
    const cancel = required<HTMLButtonElement>(
      '[data-members-dialog="invite"] footer button[type="button"]',
    );
    cancel.focus();
    await fixture.whenStable();
    expect(email.getAttribute('aria-invalid')).toBe('true');
    expect(document.activeElement).toBe(cancel);
    expect(document.querySelector('#members-invite-validation-summary')).toBeNull();
    email.focus();
    enterEmail(fixture, email, 'corrected@example.test');
    await fixture.whenStable();
    expect(email.getAttribute('aria-invalid')).toBeNull();
    expect(email.getAttribute('aria-describedby')).toBeNull();
    expect(document.activeElement).toBe(email);
    expect(store.invite).not.toHaveBeenCalled();
  });

  it('updates a failed-submit correction in place without refocusing its summary', async () => {
    const { fixture, email } = await openInvite();
    await submit(fixture);
    const summary = required<HTMLElement>('#members-invite-validation-summary');
    await vi.waitFor(() => expect(document.activeElement).toBe(summary));
    required<HTMLAnchorElement>(
      '#members-invite-validation-summary a[href="#members-invite-email"]',
    ).click();
    enterEmail(fixture, email, 'not-an-email');
    await fixture.whenStable();
    expect(summary.textContent).toContain('Enter a valid email address.');
    expect(summary.textContent).not.toContain('Enter an email address.');
    expect(document.activeElement).toBe(email);
    enterEmail(fixture, email, 'corrected@example.test');
    await fixture.whenStable();
    expect(document.querySelector('#members-invite-validation-summary')).toBeNull();
    expect(email.getAttribute('aria-invalid')).toBeNull();
    expect(email.value).toBe('corrected@example.test');
    expect(document.activeElement).toBe(email);
    expect(store.invite).not.toHaveBeenCalled();
  });

  it('resets touched and submitted feedback on a fresh opening', async () => {
    const { fixture, opener } = await openInvite();
    await submit(fixture);
    expect(document.querySelector('#members-invite-validation-summary')).not.toBeNull();
    required<HTMLButtonElement>(
      '[data-members-dialog="invite"] footer button[type="button"]',
    ).click();
    await fixture.whenStable();
    expect(document.activeElement).toBe(opener);
    opener.click();
    await fixture.whenStable();
    const email = required<HTMLInputElement>('#members-invite-email');
    expect(email.value).toBe('');
    expect(email.getAttribute('aria-invalid')).toBeNull();
    expect(email.getAttribute('aria-describedby')).toBeNull();
    expect(document.querySelector('#members-invite-validation-summary')).toBeNull();
    expect(document.activeElement).toBe(email);
    expect(store.invite).not.toHaveBeenCalled();
  });

  it('announces members loading rather than an unrelated evidence context', async () => {
    store.phase.set('loading');
    const fixture = TestBed.createComponent(MembersPage);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('app-loading-pattern')).toHaveLength(1);
    expect(root.querySelector('app-loading-pattern')?.textContent).toContain('Loading members…');
    expect(root.querySelector('app-member-list')).toBeNull();
  });
});
