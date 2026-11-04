import { InteractivityChecker } from '@angular/cdk/a11y';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { SessionStore } from '../../../core/session/session.store';
import { MembersPage } from './members.page';
import { MembersStore } from './members.store';
import { FakeMembersStore } from './members-page.test-fixtures';

const dialogs = [
  ['invite', '[data-members-invite-open]', 'Invite member'],
  ['edit-roles', '[data-members-edit-open]', 'Edit organization roles'],
  ['remove', '[data-members-remove]', 'Remove this member?'],
  ['resend', '[data-members-resend-open]', 'Renew this invitation?'],
  ['cancel-invite', '[data-members-cancel-invite-open]', 'Cancel this invitation?'],
] as const;

function required<T extends Element>(selector: string): T {
  const element = document.body.querySelector<T>(selector);
  if (!element) throw new Error(`Missing element: ${selector}`);
  return element;
}

describe('Members dialog accessibility', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [MembersPage],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: SessionStore,
          useValue: { currentOrg: signal({ id: 'org1' }), has: () => true },
        },
      ],
    });
    TestBed.overrideComponent(MembersPage, {
      set: { providers: [{ provide: MembersStore, useValue: new FakeMembersStore() }] },
    });
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  it.each(dialogs)(
    '%s uses a named modal and one focus trap; Escape restores its opener',
    async (kind, selector, label) => {
      const fixture = TestBed.createComponent(MembersPage);
      await fixture.whenStable();
      const opener = required<HTMLButtonElement>(selector);
      opener.focus();
      opener.click();
      await fixture.whenStable();
      const content = required<HTMLElement>(`[data-members-dialog="${kind}"]`);
      const modal = content.closest('[role="dialog"]');
      expect(modal?.getAttribute('aria-label')).toBe(label);
      expect(modal?.getAttribute('aria-modal')).toBe('true');
      if (kind !== 'invite') {
        const description = content.querySelector('[hlmDialogDescription]');
        expect(description?.textContent?.trim()).toBeTruthy();
        expect(description?.id).toBeTruthy();
        expect(modal?.getAttribute('aria-describedby')).toBe(description?.id);
        expect(document.getElementById(description?.id ?? '')).toBe(description);
      }
      expect(document.body.querySelectorAll('.cdk-focus-trap-anchor')).toHaveLength(2);
      expect(content.contains(document.activeElement)).toBe(true);
      for (const anchor of document.body.querySelectorAll<HTMLElement>('.cdk-focus-trap-anchor')) {
        anchor.focus();
        await fixture.whenStable();
        expect(content.contains(document.activeElement)).toBe(true);
      }
      const escape = new KeyboardEvent('keydown', {
        key: 'Escape',
        bubbles: true,
        cancelable: true,
      });
      content.dispatchEvent(escape);
      await fixture.whenStable();
      expect(escape.defaultPrevented).toBe(true);
      expect(document.body.querySelector('[data-members-dialog][data-state="open"]')).toBeNull();
      expect(document.activeElement).toBe(opener);
    },
  );

  it('associates role checkbox labels and preserves disabled save until a real change', async () => {
    const fixture = TestBed.createComponent(MembersPage);
    await fixture.whenStable();
    required<HTMLButtonElement>('[data-members-edit-open]').click();
    await fixture.whenStable();
    const checkbox = required<HTMLButtonElement>(
      '[data-members-role-option] button[role="checkbox"]',
    );
    const labelId = checkbox.getAttribute('aria-labelledby');
    expect(labelId).toBeTruthy();
    expect(document.getElementById(labelId ?? '')?.textContent).toContain(
      'Organization administrator',
    );
    expect(checkbox.getAttribute('aria-checked')).toBe('true');
    expect(required<HTMLButtonElement>('[data-members-submit]').disabled).toBe(true);
    checkbox.click();
    await fixture.whenStable();
    expect(checkbox.getAttribute('aria-checked')).toBe('false');
    expect(required<HTMLButtonElement>('[data-members-submit]').disabled).toBe(false);
  });
});
