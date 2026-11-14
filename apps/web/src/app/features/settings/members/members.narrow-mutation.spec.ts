import { InteractivityChecker } from '@angular/cdk/a11y';
import { computed, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ActivatedRoute } from '@angular/router';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { button } from './member-actions.test-helpers';
import { MemberListComponent } from './member-list.component';
import { FakeMembersStore } from './members-page.test-fixtures';
import { MembersPage } from './members.page';
import { MembersStore } from './members.store';

provideLayoutTestStubs();

const rowActions = [
  {
    output: 'remove',
    pageHandler: 'openRemove',
    rowHandler: 'requestRemove',
    selector: '[data-members-remove]',
  },
  {
    output: 'editRoles',
    pageHandler: 'openEditRoles',
    rowHandler: 'requestEditRoles',
    selector: '[data-members-edit-open]',
  },
  {
    output: 'resend',
    pageHandler: 'openResend',
    rowHandler: 'requestResend',
    selector: '[data-members-resend-open]',
  },
  {
    output: 'cancelInvite',
    pageHandler: 'openCancelInvite',
    rowHandler: 'requestCancelInvite',
    selector: '[data-members-cancel-invite-open]',
  },
] as const;

function element<T extends HTMLElement>(root: ParentNode, selector: string): T {
  const result = root.querySelector<T>(selector);
  if (!result) throw new Error(`Missing required control: ${selector}`);
  return result;
}

describe('Members synchronous narrow mutation guards', () => {
  const wide = signal(true);
  let store: FakeMembersStore;
  let fixture: ComponentFixture<MembersPage>;
  let root: HTMLElement;

  beforeEach(async () => {
    wide.set(true);
    store = new FakeMembersStore();
    TestBed.configureTestingModule({
      imports: [MembersPage],
      providers: [
        provideZonelessChangeDetection(),
        { provide: ActivatedRoute, useValue: { snapshot: { data: {} } } },
        {
          provide: SessionStore,
          useValue: { currentOrg: signal({ id: 'org1' }), has: () => true },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate: wide,
            narrow: computed(() => !wide()),
            allowsMutation: () => wide(),
          },
        },
      ],
    });
    TestBed.overrideComponent(MembersPage, {
      set: { providers: [{ provide: MembersStore, useValue: store }] },
    });
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
    fixture = TestBed.createComponent(MembersPage);
    fixture.detectChanges();
    await fixture.whenStable();
    root = document.body;
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  function emitRowAction(output: (typeof rowActions)[number]['output']): void {
    const list = fixture.debugElement.query(By.directive(MemberListComponent))
      .componentInstance as MemberListComponent;
    const member = store.members().find((value) => value.status === 'pending');
    if (!member) throw new Error('Missing pending membership fixture');
    const opener = element<HTMLButtonElement>(
      root,
      `[data-row-id="${member.id}"] [data-members-actions]`,
    );
    list[output].emit({ member, opener });
  }

  function expectNoWrites(): void {
    expect(store.invite).not.toHaveBeenCalled();
    expect(store.remove).not.toHaveBeenCalled();
    expect(store.changeRoles).not.toHaveBeenCalled();
    expect(store.resend).not.toHaveBeenCalled();
    expect(store.cancel).not.toHaveBeenCalled();
  }

  it('openInvite refuses a stale desktop button when the synchronous policy denies mutation', async () => {
    const invite = element<HTMLButtonElement>(root, '[data-members-invite-open]');
    wide.set(false);
    invite.click();
    await fixture.whenStable();
    expect(root.querySelector('[data-members-dialog][data-state="open"]')).toBeNull();
    expectNoWrites();
  });

  it.each(rowActions)(
    '$pageHandler refuses a row output when the synchronous policy denies mutation',
    async ({ output }) => {
      wide.set(false);
      emitRowAction(output);
      await fixture.whenStable();
      expect(root.querySelector('[data-members-dialog][data-state="open"]')).toBeNull();
      expectNoWrites();
    },
  );

  it.each([
    { dialog: 'invite', output: null, write: 'invite' },
    { dialog: 'remove', output: 'remove', write: 'remove' },
    { dialog: 'edit-roles', output: 'editRoles', write: 'changeRoles' },
    { dialog: 'resend', output: 'resend', write: 'resend' },
    { dialog: 'cancel-invite', output: 'cancelInvite', write: 'cancel' },
  ] as const)(
    '$dialog submit sends nothing after a desktop-open dialog narrows, then works when widened',
    async ({ dialog, output, write }) => {
      if (output === null) element<HTMLButtonElement>(root, '[data-members-invite-open]').click();
      else emitRowAction(output);
      fixture.detectChanges();
      await fixture.whenStable();
      const content = element<HTMLElement>(
        root,
        `[data-members-dialog="${dialog}"][data-state="open"]`,
      );
      if (dialog === 'invite') {
        const email = element<HTMLInputElement>(content, '#members-invite-email');
        email.value = 'new@example.test';
        email.dispatchEvent(new Event('input'));
      } else if (dialog === 'edit-roles') {
        element<HTMLButtonElement>(
          content,
          '[data-members-role-option] button[role="checkbox"]',
        ).click();
      }
      fixture.detectChanges();
      const submit = element<HTMLButtonElement>(content, '[data-members-submit]');
      expect(submit.disabled).toBe(false);
      wide.set(false);
      // Submit before change detection: the still-rendered control cannot authorize a mutation.
      submit.click();
      await fixture.whenStable();
      expectNoWrites();
      expect(content.isConnected && content.getAttribute('data-state') === 'open').toBe(true);
      wide.set(true);
      fixture.detectChanges();
      submit.click();
      await fixture.whenStable();
      expect(store[write]).toHaveBeenCalledTimes(1);
    },
  );
});

describe('Member row synchronous narrow mutation guards', () => {
  const wide = signal(true);
  let fixture: ComponentFixture<MemberListComponent>;

  beforeEach(() => {
    wide.set(true);
    TestBed.configureTestingModule({
      imports: [MemberListComponent],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate: wide,
            narrow: computed(() => !wide()),
            allowsMutation: () => wide(),
          },
        },
      ],
    });
    fixture = TestBed.createComponent(MemberListComponent);
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  it.each(rowActions)(
    '$rowHandler emits nothing from a stale desktop menu when policy denies mutation',
    async ({ output, selector }) => {
      const member = new FakeMembersStore().members().find((value) => value.status === 'pending');
      if (!member) throw new Error('Missing pending membership fixture');
      fixture.componentRef.setInput('members', [member]);
      fixture.componentRef.setInput('canWrite', true);
      const emitted = vi.fn();
      fixture.componentInstance[output].subscribe(emitted);
      await fixture.whenStable();
      const item = button(document.body, selector);
      wide.set(false);
      // The menu was opened at desktop width; the handler must recheck before emitting.
      item.click();
      expect(emitted).not.toHaveBeenCalled();
    },
  );
});
