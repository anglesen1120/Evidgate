import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { IngestTokenCreatedResponse, IngestTokenView } from '@evidgate/sdk';

import { ApiRequestError } from '../../../core/http/api-error';
import type { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { TOKEN_LIST_LIMIT } from '../onboarding-token.store';
import { OnboardingRecordDialogComponent } from '../onboarding-record-view.component';
import type { ConnectCiPage } from './connect-ci.page';
import {
  TOKEN,
  SECRET,
  ROTATED,
  createConnectionPageContext,
  failure,
} from './connect-ci.page.test-support';
import { requiredElement } from './step-controls.testing';

describe('ConnectCiPage reveal, rotation and metadata boundaries', () => {
  const context = createConnectionPageContext();
  const {
    permitted,
    tokens,
    tokensTruncated,
    refreshFailure,
    createIngestToken,
    rotateIngestToken,
    refresh,
    fill,
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
    await submit();
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
    await submit();
    expect(slot.current()?.value).toBe(SECRET);
    expect(root.outerHTML).not.toContain(SECRET);
    expect(root.textContent).not.toContain(SECRET);
    fixture.destroy();
    expect(slot.current()).toBeNull();
    expect(document.body.innerHTML).not.toContain(SECRET);
  });

  it.each(['projectId', 'orgId'])('clears the reveal on a changed %s input', async (input) => {
    await fill({ 'connect-token-name': 'Build upload' });
    await submit();
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
    await submit();
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
    tokens.set([TOKEN]);
    await fixture.whenStable();
    const row = requiredElement<HTMLTableRowElement>(root, '[data-testid="connect-token-row"]');
    expect(row.textContent).toContain(TOKEN.name);
    expect(row.textContent).toContain(TOKEN.prefix);

    // This unit mounts the step directly. Wire its View output to the same dialog the shell owns.
    const viewer = TestBed.createComponent(OnboardingRecordDialogComponent);
    viewer.componentRef.setInput('scope', `org-1:${TOKEN.project_id}`);
    viewer.componentRef.setInput('progress', null);
    viewer.componentRef.setInput('step', 2);
    fixture.componentInstance.viewRecord.subscribe((record) =>
      viewer.componentInstance.open(record),
    );
    await viewer.whenStable();
    requiredElement<HTMLButtonElement>(row, `button[aria-label="View ${TOKEN.name}"]`).click();
    await viewer.whenStable();
    const modal = requiredElement(document.body, '[role="dialog"]');
    expect(modal.textContent).toContain(TOKEN.name);
    for (const scope of TOKEN.scopes) expect(modal.textContent).toContain(scope);
    expect(modal.textContent).toContain('Expires (UTC)');
    expect(modal.textContent).toContain('Last used (UTC)');
    expect(modal.outerHTML).not.toContain(SECRET);
    expect(refresh).not.toHaveBeenCalled();
    expect(slot.current()).toBeNull();
    expect(root.outerHTML).not.toContain(SECRET);
    expect(createIngestToken).not.toHaveBeenCalled();
    expect(rotateIngestToken).not.toHaveBeenCalled();
  });

  it('keeps mutation controls visible but disabled with linked permission reasons', async () => {
    tokens.set([TOKEN]);
    permitted.set(false);
    await fixture.whenStable();
    for (const id of ['connect-token-submit', 'connect-token-rotate']) {
      const button = root.querySelector<HTMLButtonElement>(`[data-testid="${id}"]`)!;
      expect(button.disabled).toBe(true);
      const descriptionIds = button.getAttribute('aria-describedby')!.split(' ');
      const permissionId = 'connect-token-permission';
      expect(descriptionIds).toContain(permissionId);
      expect(root.querySelector(`#${permissionId}`)?.textContent?.trim()).not.toBe('');
    }
    await submit();
    expect(createIngestToken).not.toHaveBeenCalled();
    expect(root.querySelector('[data-testid="connect-token-row"]')).not.toBeNull();
  });

  it('rotates an active token, reveals the successor and uses the supplied overlap timestamp', async () => {
    tokens.set([TOKEN]);
    await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('[data-testid="connect-token-rotate"]')!.click();
    await fixture.whenStable();
    expect(rotateIngestToken).not.toHaveBeenCalled();
    document.querySelector<HTMLButtonElement>('[data-testid="connect-rotation-confirm"]')!.click();
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

  it('retains the reveal when token inventory refresh fails and retries only metadata', async () => {
    refresh.mockImplementationOnce(() => {
      refreshFailure.set('unavailable');
      return Promise.resolve();
    });
    await fill({ 'connect-token-name': 'Build upload' });
    await submit();
    expect(slot.current()?.value).toBe(SECRET);
    const retry = requiredElement<HTMLButtonElement>(
      root,
      '[data-testid="connect-ci-refresh-failed"] button',
    );
    requiredElement<HTMLButtonElement>(root, '[data-testid="reveal-dismiss"]').click();
    await fixture.whenStable();
    expect(document.activeElement).toBe(retry);
    retry.click();
    await fixture.whenStable();
    expect(refresh).toHaveBeenCalledTimes(2);
    expect(createIngestToken).toHaveBeenCalledTimes(1);
    expect(root.querySelector('[data-testid="connect-ci-refresh-failed"]')).toBeNull();
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
      await submit();
      createIngestToken.mockRejectedValueOnce(failure('conflict', 409));
      await submit();
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

  it('focuses the token reveal heading, announces no value, and restores the panel heading on Dismiss', async () => {
    const idleAnnouncement = root.querySelector('[data-testid="connect-reveal-status"]');
    expect(idleAnnouncement?.classList.contains('sr-only')).toBe(true);
    await fill({ 'connect-token-name': 'Build upload' });
    await submit();
    const value = root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]')!;
    const heading = root.querySelector(`#${value.getAttribute('aria-labelledby')}`)!;
    expect(document.activeElement).toBe(heading);
    expect(heading.getAttribute('tabindex')).toBe('-1');
    const announcement = root.querySelector('[data-testid="connect-reveal-status"]')!;
    expect(announcement.getAttribute('aria-live')).toBe('polite');
    expect(announcement.textContent?.trim()).not.toBe('');
    expect(announcement.textContent).not.toContain(value.value);
    expect(announcement).toBe(idleAnnouncement);
    expect(announcement.classList.contains('sr-only')).toBe(false);
    root.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]')!.click();
    await fixture.whenStable();
    expect(document.activeElement).toBe(root.querySelector('#connect-token-heading'));
    expect(announcement.textContent?.trim()).toBe('');
    expect(root.querySelector('[data-testid="connect-reveal-status"]')).toBe(announcement);
    expect(announcement.classList.contains('sr-only')).toBe(true);
  });

  it.each(['timeout', 'network'] as const)(
    'reconciles metadata once after a %s on create/rotate without replaying the command',
    async (kind) => {
      for (const action of ['token', 'rotate'] as const) {
        refresh.mockClear();
        createIngestToken.mockClear();
        rotateIngestToken.mockClear();
        const error = failure(kind, 0);
        // Transport errors may be retryable for reads, never for these one-time commands.
        const uncertain = new ApiRequestError({ ...error.error, retryable: true });
        refresh.mockImplementationOnce(() => {
          tokens.set([TOKEN]);
          return Promise.resolve();
        });
        if (action === 'token') {
          createIngestToken.mockRejectedValueOnce(uncertain);
          await fill({ 'connect-token-name': 'Build upload' });
          await submit();
        } else {
          rotateIngestToken.mockRejectedValueOnce(uncertain);
          root.querySelector<HTMLButtonElement>('[data-testid="connect-token-rotate"]')!.click();
          await fixture.whenStable();
          expect(rotateIngestToken).not.toHaveBeenCalled();
          document
            .querySelector<HTMLButtonElement>('[data-testid="connect-rotation-confirm"]')!
            .click();
          // Dialog close emits consent asynchronously; its first stable render can still be busy.
          await vi.waitFor(() => expect(refresh).toHaveBeenCalledOnce());
          await fixture.whenStable();
        }
        expect(refresh).toHaveBeenCalledExactlyOnceWith('org-1', 'project-1');
        expect(root.querySelector('[data-testid="connect-token-row"]')).not.toBeNull();
        const hint = root.querySelector('[data-testid="connect-ci-response-uncertain"]');
        expect(hint).not.toBeNull();
        expect(hint?.textContent?.trim()).not.toBe('');
        expect(root.querySelector('[data-testid="connect-ci-error"] button')).toBeNull();
        expect(slot.current()).toBeNull();
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
      await submit();
      fixture.componentRef.setInput(input, 'scope-2');
      await fixture.whenStable();
      const button = root.querySelector<HTMLButtonElement>('[data-testid="connect-token-submit"]')!;
      expect(button.disabled).toBe(false);
      expect(
        root.querySelector<HTMLInputElement>('[data-testid="connect-token-name"]')?.value,
      ).toBe('');
      await fill({ 'connect-token-name': 'Build upload in the new scope' });
      await submit();
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

  it.each(['other project', 're-entered project'] as const)(
    'reconciles a stale timeout only when the issuing project is active: %s',
    async (destination) => {
      let rejectOld!: (error: ApiRequestError) => void;
      createIngestToken.mockImplementationOnce(
        () =>
          new Promise((_, reject) => {
            rejectOld = reject;
          }),
      );
      await fill({ 'connect-token-name': 'Build upload' });
      await submit();
      if (destination === 'other project') {
        fixture.componentRef.setInput('projectId', 'project-2');
      } else {
        fixture.componentRef.setInput('active', false);
        await fixture.whenStable();
        fixture.componentRef.setInput('active', true);
      }
      await fixture.whenStable();
      rejectOld(failure('timeout', 0));
      await fixture.whenStable();
      if (destination === 'other project') expect(refresh).not.toHaveBeenCalled();
      else
        await vi.waitFor(() =>
          expect(refresh).toHaveBeenCalledExactlyOnceWith('org-1', 'project-1'),
        );
      expect(createIngestToken).toHaveBeenCalledTimes(1);
      expect(slot.current()).toBeNull();
      expect(root.querySelector('[data-testid="connect-ci-error"]')).toBeNull();
      expect(root.querySelector('[data-testid="connect-ci-response-uncertain"]')).toBeNull();
    },
  );

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
    expect(root.querySelector('[data-testid="connect-tokens-truncated"]')).toBeNull();
    tokensTruncated.set(true);
    await fixture.whenStable();
    const tokenNotice = root.querySelector(
      '[data-testid="connect-token-panel"] [data-testid="connect-tokens-truncated"]',
    );
    expect(tokenNotice).not.toBeNull();
    expect(tokenNotice?.textContent).toContain(String(TOKEN_LIST_LIMIT));
  });
});
