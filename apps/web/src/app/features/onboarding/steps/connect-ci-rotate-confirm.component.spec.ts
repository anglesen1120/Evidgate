import { InteractivityChecker } from '@angular/cdk/a11y';
import { TestBed } from '@angular/core/testing';
import { TOKEN, ROTATED, createConnectionPageContext } from './connect-ci.page.test-support';
import { requiredElement } from './step-controls.testing';

// Exercise the real page handler as well as the dialog: consent alone must not bypass its guards.
describe('Onboarding token rotation explicit consent', () => {
  const context = createConnectionPageContext();

  beforeEach(async () => {
    await context.mount();
    // jsdom has no layout; retain CDK focus behavior apart from visibility detection.
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
    context.tokens.set([TOKEN]);
    await context.fixture.whenStable();
  });

  afterEach(() => vi.restoreAllMocks());

  async function open() {
    const opener = requiredElement<HTMLButtonElement>(
      context.root,
      '[data-testid="connect-token-rotate"]',
    );
    opener.focus();
    opener.click();
    await context.fixture.whenStable();
    expect(context.rotateIngestToken).not.toHaveBeenCalled();
    const dialog = requiredElement<HTMLElement>(document.body, '[role="alertdialog"]');
    return { opener, dialog };
  }

  it('shows the credential and consequence before sending exactly one confirmed request', async () => {
    const name = 'Production CI '.repeat(20) + '<img src=x onerror=bad()>';
    context.tokens.set([{ ...TOKEN, name }]);
    await context.fixture.whenStable();
    const { dialog } = await open();
    const text = dialog.textContent?.replace(/\s+/g, ' ');
    expect(text).toContain(name);
    expect(text).toContain('acme-web');
    expect(text).toContain(TOKEN.prefix);
    for (const scope of TOKEN.scopes) expect(text).toContain(scope);
    expect(text).toContain('previous token stops working');
    expect(text).toContain('overlap window is not guaranteed');
    expect(text).toContain('does not update CI for you');
    expect(dialog.querySelector('img')).toBeNull();
    expect(document.activeElement).toBe(
      dialog.querySelector('[data-testid="connect-rotation-cancel"]'),
    );
    expect(context.slot.current()).toBeNull();
    const confirm = requiredElement<HTMLButtonElement>(
      dialog,
      '[data-testid="connect-rotation-confirm"]',
    );
    confirm.click();
    confirm.click();
    await context.fixture.whenStable();
    expect(context.rotateIngestToken).toHaveBeenCalledExactlyOnceWith(TOKEN.id);
    expect(document.querySelector('[role="alertdialog"]')).toBeNull();
    expect(context.slot.current()).toEqual({ source: 'ingest-token-rotation', value: ROTATED });
    expect(context.root.querySelectorAll('[data-testid="reveal-value"]')).toHaveLength(1);
  });

  it.each(['cancel', 'escape'] as const)(
    'sends no request on %s and restores focus to the trigger',
    async (action) => {
      const { opener, dialog } = await open();
      if (action === 'cancel') {
        requiredElement<HTMLButtonElement>(
          dialog,
          '[data-testid="connect-rotation-cancel"]',
        ).click();
      } else {
        dialog.dispatchEvent(
          new KeyboardEvent('keydown', {
            key: 'Escape',
            keyCode: 27,
            bubbles: true,
            cancelable: true,
          }),
        );
      }
      await context.fixture.whenStable();
      expect(context.rotateIngestToken).not.toHaveBeenCalled();
      expect(context.slot.current()).toBeNull();
      expect(document.querySelector('[role="alertdialog"]')).toBeNull();
      expect(document.activeElement).toBe(opener);
    },
  );

  it.each(['organisation', 'project', 'permission', 'step', 'status'] as const)(
    'rejects stale consent after a %s change even before the next render',
    async (change) => {
      const { dialog } = await open();
      const confirm = requiredElement<HTMLButtonElement>(
        dialog,
        '[data-testid="connect-rotation-confirm"]',
      );
      if (change === 'organisation') context.fixture.componentRef.setInput('orgId', 'org-2');
      if (change === 'project') context.fixture.componentRef.setInput('projectId', 'project-2');
      if (change === 'permission') context.permitted.set(false);
      if (change === 'step') context.fixture.componentRef.setInput('active', false);
      if (change === 'status') context.tokens.set([{ ...TOKEN, status: 'rotated' }]);
      confirm.click();
      await context.fixture.whenStable();
      expect(context.rotateIngestToken).not.toHaveBeenCalled();
      expect(context.slot.current()).toBeNull();
      expect(document.querySelector('[role="alertdialog"]')).toBeNull();
    },
  );
});
