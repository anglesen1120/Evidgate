import type { ComponentFixture } from '@angular/core/testing';
import type { IngestTokenCreatedResponse, IngestTokenView } from '@evidgate/sdk';

import { ApiRequestError } from '../../../core/http/api-error';
import type { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { CONNECT_LIST_LIMIT } from '../data/onboarding-progress.service';
import type { ConnectCiPage } from './connect-ci.page';
import {
  BINDING,
  TOKEN,
  CHALLENGE,
  SECRET,
  ROTATED,
  createConnectionPageContext,
  failure,
} from './connect-ci.page.test-support';

describe('ConnectCiPage reveal, rotation and metadata boundaries', () => {
  const context = createConnectionPageContext();
  const {
    permitted,
    bindings,
    tokens,
    bindingsTruncated,
    tokensTruncated,
    refreshFailure,
    canGoNext,
    createCiBinding,
    createIngestToken,
    rotateIngestToken,
    refresh,
    fill,
    fillOidc,
    submit,
  } = context;
  let fixture: ComponentFixture<ConnectCiPage>;
  let root: HTMLElement;
  let slot: RevealSlot;

  beforeEach(async () => {
    await context.mount();
    fixture = context.fixture;
    root = context.root;
    slot = context.slot;
  });
  it('dismisses the reveal without keeping a copy in metadata', async () => {
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('token');
    root.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]')!.click();
    await fixture.whenStable();
    expect(slot.current()).toBeNull();
    expect(root.querySelector('[data-testid="connect-reveal"]')).toBeNull();
    expect(root.outerHTML).not.toContain(SECRET);
    expect(root.querySelector('[data-testid="connect-token-row"]')?.textContent).toContain(
      'egt_prefix',
    );
  });

  it('UNMOUNT clears the page-scoped slot and never serializes the secret into DOM markup', async () => {
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('token');
    expect(slot.current()?.value).toBe(SECRET);
    expect(root.outerHTML).not.toContain(SECRET);
    expect(root.textContent).not.toContain(SECRET);
    fixture.destroy();
    expect(slot.current()).toBeNull();
    expect(document.body.innerHTML).not.toContain(SECRET);
  });

  it.each(['projectId', 'orgId'])('clears the reveal on a changed %s input', async (input) => {
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('token');
    fixture.componentRef.setInput(input, 'different-scope');
    await fixture.whenStable();
    expect(slot.current()).toBeNull();
    expect(root.querySelector('[data-testid="connect-reveal"]')).toBeNull();
  });

  it('does not reveal or refresh a late create response after a scope change or unmount', async () => {
    let resolvePending!: (response: IngestTokenCreatedResponse) => void;
    createIngestToken.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolvePending = resolve;
        }),
    );
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('token');
    fixture.componentRef.setInput('projectId', 'project-2');
    await fixture.whenStable();
    fixture.destroy();
    resolvePending({ token: TOKEN, secret: SECRET });
    await Promise.resolve();
    await Promise.resolve();
    expect(slot.current()).toBeNull();
    expect(refresh).not.toHaveBeenCalled();
  });

  it('redisplays metadata only on resume without issuing anything', async () => {
    bindings.set([BINDING]);
    tokens.set([TOKEN]);
    await fixture.whenStable();
    for (const value of [
      'acme/web',
      'Build upload',
      'egt_prefix',
      'event:write',
      'Expires (UTC)',
      'Last used (UTC)',
    ]) {
      expect(root.textContent).toContain(value);
    }
    expect(slot.current()).toBeNull();
    expect(root.outerHTML).not.toContain(SECRET);
    expect(root.outerHTML).not.toContain(CHALLENGE);
    expect(createCiBinding).not.toHaveBeenCalled();
    expect(createIngestToken).not.toHaveBeenCalled();
    expect(rotateIngestToken).not.toHaveBeenCalled();
  });

  it('keeps mutation controls visible but disabled with linked permission reasons', async () => {
    tokens.set([TOKEN]);
    bindings.set([BINDING]);
    permitted.set(false);
    await fixture.whenStable();
    for (const id of ['connect-oidc-submit', 'connect-token-submit', 'connect-token-rotate']) {
      const button = root.querySelector<HTMLButtonElement>(`[data-testid="${id}"]`)!;
      expect(button.disabled).toBe(true);
      const descriptionIds = button.getAttribute('aria-describedby')!.split(' ');
      const permissionId =
        id === 'connect-oidc-submit' ? 'connect-binding-permission' : 'connect-token-permission';
      expect(descriptionIds).toContain(permissionId);
      expect(root.querySelector(`#${permissionId}`)?.textContent?.trim()).not.toBe('');
    }
    await fillOidc();
    await submit('oidc');
    await submit('token');
    expect(createCiBinding).not.toHaveBeenCalled();
    expect(createIngestToken).not.toHaveBeenCalled();
    expect(root.querySelector('[data-testid="connect-token-row"]')).not.toBeNull();
  });

  it('rotates an active token, reveals the successor and uses the supplied overlap timestamp', async () => {
    tokens.set([TOKEN]);
    await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('[data-testid="connect-token-rotate"]')!.click();
    await fixture.whenStable();
    expect(rotateIngestToken).toHaveBeenCalledExactlyOnceWith('token-1');
    expect(slot.current()).toEqual({ source: 'ingest-token-rotation', value: ROTATED });
    expect(root.querySelector('time[datetime="2026-10-27T07:30:00Z"]')).not.toBeNull();
    expect(refresh).toHaveBeenCalledWith('org-1', 'project-1');
  });

  it('offers no rotation on non-active token metadata', async () => {
    tokens.set(
      ['rotated', 'expired', 'revoked'].map((status, index) => ({
        ...TOKEN,
        id: `token-${index}`,
        status: status as IngestTokenView['status'],
      })),
    );
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="connect-token-rotate"]')).toBeNull();
  });

  it('retains the reveal when progress refresh fails and retries only metadata', async () => {
    refresh.mockImplementationOnce(() => {
      refreshFailure.set('unavailable');
      return Promise.resolve();
    });
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('token');
    expect(slot.current()?.value).toBe(SECRET);
    expect(canGoNext()).toBe(false);
    root
      .querySelector<HTMLButtonElement>('[data-testid="connect-ci-refresh-failed"] button')!
      .click();
    await fixture.whenStable();
    expect(refresh).toHaveBeenCalledTimes(2);
    expect(createIngestToken).toHaveBeenCalledTimes(1);
    expect(root.querySelector('[data-testid="connect-ci-refresh-failed"]')).toBeNull();
    expect(canGoNext()).toBe(true);
  });

  it('keeps the first reveal copyable when a second create fails with 409', async () => {
    const originalClipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    try {
      await fill({ 'connect-token-name': 'Build upload' });
      await submit('token');
      createIngestToken.mockRejectedValueOnce(failure('conflict', 409));
      await submit('token');
      expect(slot.current()).toEqual({ source: 'ingest-token', value: SECRET });
      expect(root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]')?.value).toBe(
        SECRET,
      );
      root.querySelector<HTMLButtonElement>('[data-testid="reveal-copy"]')!.click();
      await fixture.whenStable();
      expect(writeText).toHaveBeenCalledExactlyOnceWith(SECRET);
      expect(createIngestToken).toHaveBeenCalledTimes(2);
      expect(refresh).toHaveBeenCalledTimes(1);
    } finally {
      if (originalClipboard) Object.defineProperty(navigator, 'clipboard', originalClipboard);
      else Reflect.deleteProperty(navigator, 'clipboard');
    }
  });

  it.each(['oidc', 'token'] as const)(
    'focuses the %s reveal heading, announces no value, and restores the panel heading on Dismiss',
    async (panel) => {
      if (panel === 'oidc') await fillOidc();
      else await fill({ 'connect-token-name': 'Build upload' });
      await submit(panel);
      const value = root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]')!;
      const heading = root.querySelector(`#${value.getAttribute('aria-labelledby')}`)!;
      expect(document.activeElement).toBe(heading);
      expect(heading.getAttribute('tabindex')).toBe('-1');
      const announcement = root.querySelector('[data-testid="connect-reveal-status"]')!;
      expect(announcement.getAttribute('aria-live')).toBe('polite');
      expect(announcement.textContent?.trim()).not.toBe('');
      expect(announcement.textContent).not.toContain(value.value);
      root.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]')!.click();
      await fixture.whenStable();
      expect(document.activeElement).toBe(root.querySelector(`#connect-${panel}-heading`));
      expect(announcement.textContent?.trim()).toBe('');
    },
  );

  it.each(['timeout', 'network'] as const)(
    'reconciles metadata once after a %s on create/rotate without replaying the command',
    async (kind) => {
      for (const action of ['oidc', 'token', 'rotate'] as const) {
        refresh.mockClear();
        createCiBinding.mockClear();
        createIngestToken.mockClear();
        rotateIngestToken.mockClear();
        const error = failure(kind, 0);
        // Transport errors may be retryable for reads, never for these one-time commands.
        const uncertain = new ApiRequestError({ ...error.error, retryable: true });
        refresh.mockImplementationOnce(() => {
          tokens.set([TOKEN]);
          return Promise.resolve();
        });
        if (action === 'oidc') {
          createCiBinding.mockRejectedValueOnce(uncertain);
          await fillOidc();
          await submit('oidc');
        } else if (action === 'token') {
          createIngestToken.mockRejectedValueOnce(uncertain);
          await fill({ 'connect-token-name': 'Build upload' });
          await submit('token');
        } else {
          rotateIngestToken.mockRejectedValueOnce(uncertain);
          root.querySelector<HTMLButtonElement>('[data-testid="connect-token-rotate"]')!.click();
          await fixture.whenStable();
        }
        expect(refresh).toHaveBeenCalledExactlyOnceWith('org-1', 'project-1');
        expect(root.querySelector('[data-testid="connect-token-row"]')).not.toBeNull();
        const hint = root.querySelector('[data-testid="connect-ci-response-uncertain"]');
        expect(hint).not.toBeNull();
        expect(hint?.textContent?.trim()).not.toBe('');
        expect(root.querySelector('[data-testid="connect-ci-error"] button')).toBeNull();
        expect(slot.current()).toBeNull();
        expect(createCiBinding).toHaveBeenCalledTimes(action === 'oidc' ? 1 : 0);
        expect(createIngestToken).toHaveBeenCalledTimes(action === 'token' ? 1 : 0);
        expect(rotateIngestToken).toHaveBeenCalledTimes(action === 'rotate' ? 1 : 0);
      }
    },
  );

  it.each(['orgId', 'projectId'])(
    'resets busy on changed %s and ignores the stale completion while the new command is busy',
    async (input) => {
      // ES2022 project lib does not expose Promise.withResolvers.
      let resolveOld!: (response: IngestTokenCreatedResponse) => void;
      let resolveNew!: (response: IngestTokenCreatedResponse) => void;
      createIngestToken
        .mockImplementationOnce(
          () =>
            new Promise((resolve) => {
              resolveOld = resolve;
            }),
        )
        .mockImplementationOnce(
          () =>
            new Promise((resolve) => {
              resolveNew = resolve;
            }),
        );
      await fill({ 'connect-token-name': 'Build upload' });
      await submit('token');
      fixture.componentRef.setInput(input, 'scope-2');
      await fixture.whenStable();
      const button = root.querySelector<HTMLButtonElement>('[data-testid="connect-token-submit"]')!;
      expect(button.disabled).toBe(false);
      expect(
        root.querySelector<HTMLInputElement>('[data-testid="connect-token-name"]')?.value,
      ).toBe('');
      await fill({ 'connect-token-name': 'Build upload in the new scope' });
      await submit('token');
      expect(createIngestToken).toHaveBeenCalledTimes(2);
      resolveOld({ token: TOKEN, secret: SECRET });
      await fixture.whenStable();
      expect(slot.current()).toBeNull();
      expect(refresh).not.toHaveBeenCalled();
      expect(button.disabled).toBe(true);
      resolveNew({ token: TOKEN, secret: ROTATED });
      await fixture.whenStable();
      expect(slot.current()?.value).toBe(ROTATED);
      await fixture.whenStable();
      expect(
        root.querySelector<HTMLButtonElement>('[data-testid="connect-token-submit"]')!.disabled,
      ).toBe(false);
      expect(refresh).toHaveBeenCalledExactlyOnceWith(
        input === 'orgId' ? 'scope-2' : 'org-1',
        input === 'projectId' ? 'scope-2' : 'project-1',
      );
    },
  );

  it('does not reconcile a stale timeout after the project changes', async () => {
    let rejectOld!: (error: ApiRequestError) => void;
    createIngestToken.mockImplementationOnce(
      () =>
        new Promise((_, reject) => {
          rejectOld = reject;
        }),
    );
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('token');
    fixture.componentRef.setInput('projectId', 'project-2');
    await fixture.whenStable();
    rejectOld(failure('timeout', 0));
    await fixture.whenStable();
    expect(refresh).not.toHaveBeenCalled();
    expect(root.querySelector('[data-testid="connect-ci-error"]')).toBeNull();
    expect(root.querySelector('[data-testid="connect-ci-response-uncertain"]')).toBeNull();
  });

  it('describes each Rotate button with its own token name', async () => {
    tokens.set([TOKEN, { ...TOKEN, id: 'token-2', name: 'Other workflow' }]);
    await fixture.whenStable();
    const buttons = [...root.querySelectorAll('[data-testid="connect-token-rotate"]')];
    expect(buttons).toHaveLength(2);
    expect(buttons.map((button) => button.getAttribute('aria-describedby'))).toEqual([
      'connect-token-name-token-1',
      'connect-token-name-token-2',
    ]);
    // The visible label is the same on every row, so each button names its own token.
    expect(buttons.map((button) => button.getAttribute('aria-label'))).toEqual([
      'Rotate token Build upload',
      'Rotate token Other workflow',
    ]);
    for (const button of buttons) {
      const row = button.closest('[data-testid="connect-token-row"]')!;
      expect(row.querySelector(`#${button.getAttribute('aria-describedby')}`)).toBe(
        row.querySelector('strong'),
      );
    }
  });

  it('shows a truncation notice only under each truncated metadata list', async () => {
    expect(root.querySelector('[data-testid="connect-bindings-truncated"]')).toBeNull();
    expect(root.querySelector('[data-testid="connect-tokens-truncated"]')).toBeNull();
    bindingsTruncated.set(true);
    await fixture.whenStable();
    const bindingNotice = root.querySelector(
      '[data-testid="connect-oidc-panel"] [data-testid="connect-bindings-truncated"]',
    );
    expect(bindingNotice).not.toBeNull();
    // The page size the service reads is the one the notice names; no second literal.
    expect(bindingNotice?.textContent).toContain(String(CONNECT_LIST_LIMIT));
    expect(bindingNotice?.textContent).not.toContain('first 10');
    expect(root.querySelector('[data-testid="connect-tokens-truncated"]')).toBeNull();
    bindingsTruncated.set(false);
    tokensTruncated.set(true);
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="connect-bindings-truncated"]')).toBeNull();
    const tokenNotice = root.querySelector(
      '[data-testid="connect-token-panel"] [data-testid="connect-tokens-truncated"]',
    );
    expect(tokenNotice).not.toBeNull();
    expect(tokenNotice?.textContent).toContain(String(CONNECT_LIST_LIMIT));
  });
});
