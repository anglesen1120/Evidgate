import { TestBed } from '@angular/core/testing';

/** Exercise the row menu before choosing an action, including its persistent focus-return trigger. */
export function memberAction(root: ParentNode, selector: string): HTMLButtonElement | null {
  if (!/^\[data-members-(edit-open|remove|resend-open|cancel-invite-open)\]$/.test(selector))
    return null;
  const pending = selector.includes('resend') || selector.includes('cancel-invite');
  const trigger = root.querySelector<HTMLButtonElement>(
    pending ? '[data-members-actions][data-members-status="pending"]' : '[data-members-actions]',
  );
  if (!trigger) throw new Error(`Missing member action menu: ${selector}`);
  trigger.click();
  TestBed.tick();
  const action = document.body.querySelector<HTMLButtonElement>(selector);
  if (!action) throw new Error(`Missing member action: ${selector}`);
  return action;
}

export function activeMemberMenuTrigger(): HTMLButtonElement {
  const trigger = document.body.querySelector<HTMLButtonElement>(
    '[data-members-actions][aria-expanded="true"]',
  );
  if (!trigger) throw new Error('Expected an open member action menu');
  return trigger;
}

export function button(root: ParentNode, selector: string): HTMLButtonElement {
  const value = root.querySelector<HTMLButtonElement>(selector) ?? memberAction(root, selector);
  if (!value) throw new Error(`Missing button: ${selector}`);
  return value;
}
