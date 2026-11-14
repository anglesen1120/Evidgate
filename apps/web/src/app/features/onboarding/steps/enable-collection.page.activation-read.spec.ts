import type { PolicyActivationStateDto } from '@evidgate/sdk';

import {
  ACTIVATION_STATE,
  ACTIVATION_STATE_ENFORCING,
  confirmButton,
  createActivationReadContext,
  deferredResult,
  drain,
  failure,
  readRetryAction,
  retryButton,
  submit,
  text,
} from './enable-collection.page.test-support';

describe('EnableCollectionPage activation read', () => {
  const { permitted, createPolicyRevision, activatePolicy, readPolicyActivation, createPage } =
    createActivationReadContext();

  it('restores the terminal panel from the read alone and sends no command', async () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem');
    readPolicyActivation.mockResolvedValue(ACTIVATION_STATE);
    const fixture = await createPage();
    const root = fixture.nativeElement as HTMLElement;

    const enabled = root.querySelector('[data-testid="enable-collection-enabled"]');
    expect(enabled).not.toBeNull();
    expect(enabled!.textContent).toContain('Project enabled and collecting');
    // Every field the panel names comes from the read: project, revision, hash, mode, actor, time.
    expect(enabled!.textContent).toContain('project-1');
    expect(enabled!.textContent).toContain(ACTIVATION_STATE.policy_revision_id);
    expect(enabled!.textContent).toContain('sha256:9f2c1d7b4a6e…');
    expect(enabled!.textContent).toContain('Advisory');
    expect(enabled!.textContent).toContain('user-1');
    expect(enabled!.querySelector('time')?.getAttribute('datetime')).toBe(
      ACTIVATION_STATE.activated_at,
    );
    expect(enabled!.textContent).toContain('built from the activation recorded on the server');
    // No confirm and no preview: an active policy means there is nothing to append.
    expect(confirmButton(root)).toBeNull();
    expect(root.querySelector('[data-testid="enable-collection-policy-preview"]')).toBeNull();
    expect(root.querySelector('[data-testid="enable-collection-acknowledge"]')).toBeNull();
    // The read is the ONLY call: a reload never re-activates.
    expect(readPolicyActivation).toHaveBeenCalledExactlyOnceWith('project-1');
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
    // No local memory of the completion either (no storage, no cookie).
    expect(setItem).not.toHaveBeenCalled();
    expect(document.cookie).toBe('');
    setItem.mockRestore();
  });

  it('renders an enforcing mode verbatim and never the advisory claim', async () => {
    readPolicyActivation.mockResolvedValue(ACTIVATION_STATE_ENFORCING);
    const fixture = await createPage();
    const root = fixture.nativeElement as HTMLElement;

    const enabled = root.querySelector('[data-testid="enable-collection-enabled"]')!;
    expect(enabled.textContent).toContain('enforcing');
    expect(enabled.textContent).not.toContain('Not gating yet');
    expect(enabled.textContent).not.toContain('advisory mode');
    expect(enabled.textContent).not.toContain('Advisory');
    // The value is reported, not a claim beyond it: no gating statement is invented either.
    expect(enabled.textContent).not.toContain('blocks a merge');
    expect(confirmButton(root)).toBeNull();
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
  });

  it.each([
    ['a right-to-left override', '\u202E'],
    ['a C1 control', '\u009B'],
  ])(
    'strips %s a malformed mode carries before it reaches the mode row',
    async (_shape, invisible) => {
      // A closed enum cannot carry this, so the value is cast: the page still renders the answer,
      // and an invisible character in it could reorder the row that names the mode.
      const hostileMode = `audit${invisible}` as PolicyActivationStateDto['mode'];
      readPolicyActivation.mockResolvedValue({ ...ACTIVATION_STATE, mode: hostileMode });
      const fixture = await createPage();
      const root = fixture.nativeElement as HTMLElement;

      const enabled = root.querySelector('[data-testid="enable-collection-enabled"]')!;
      expect(enabled.textContent).not.toContain(invisible);
      expect(enabled.textContent).not.toContain('\u202E');
      expect(enabled.textContent).not.toContain('\u009B');
      // The sanitised wire value is reported, not a translated label claiming a mode we do not
      // offer, and it is isolated in its own `<bdi>` so it cannot reorder the row around it.
      const modeRow = Array.from(enabled.querySelectorAll('div')).find(
        (row) => row.querySelector('dt')?.textContent?.trim() === 'Mode',
      );
      expect(modeRow?.querySelector('bdi')?.textContent?.trim()).toBe('audit');
      expect(enabled.textContent).not.toContain('Not gating yet');
    },
  );

  it('never renders bidi or control characters an activation read carries', async () => {
    // Every server-provided member of the read is hostile at once: the digest (sanitised before it
    // is abbreviated), the actor, and the revision id.
    readPolicyActivation.mockResolvedValue({
      ...ACTIVATION_STATE,
      hash: 'sha256:9f2c\u202E\u009B1d7b4a6e8f0a',
      activated_by: 'user-\u202E1\u009B',
      policy_revision_id: '018f\u202E0000-0000-7000-8000-00000000a004\u009B',
    });
    const fixture = await createPage();
    const root = fixture.nativeElement as HTMLElement;

    const enabled = root.querySelector('[data-testid="enable-collection-enabled"]')!;
    expect(enabled.textContent).not.toContain('\u202E');
    expect(enabled.textContent).not.toContain('\u009B');
    // Stripped, not replaced by "unavailable": the identifying values survive without the invisible
    // characters that could make one revision read as another.
    expect(enabled.textContent).toContain('sha256:9f2c1d7b4a6e…');
    expect(enabled.textContent).toContain('user-1');
    expect(enabled.textContent).toContain('018f0000-0000-7000-8000-00000000a004');
    // Interpolated text, never markup — and isolated so no member can reorder the row it sits in.
    expect(Array.from(enabled.querySelectorAll('code > bdi')).length).toBeGreaterThanOrEqual(3);
    expect(enabled.querySelector('code')?.innerHTML).not.toContain('&lt;');
  });

  it('keeps the confirm disabled until the read settles', async () => {
    const deferred = deferredResult<PolicyActivationStateDto | null>();
    readPolicyActivation.mockImplementation(() => deferred.promise);
    const fixture = await createPage();
    const root = fixture.nativeElement as HTMLElement;

    // Loading: the control exists but cannot be pressed, and it says why.
    expect(confirmButton(root)).not.toBeNull();
    expect(confirmButton(root)!.disabled).toBe(true);
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
    submit(root);
    await drain(fixture);
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();

    deferred.resolve(null);
    await drain(fixture);
    // The read answered "no activation": now — and only now — the confirm is live.
    expect(confirmButton(root)!.disabled).toBe(false);
    expect(confirmButton(root)!.getAttribute('aria-describedby')).toBe('enable-replaces-warning');
  });

  it('offers the confirm when the read answers no activation (404)', async () => {
    readPolicyActivation.mockResolvedValue(null);
    const fixture = await createPage();
    const root = fixture.nativeElement as HTMLElement;

    expect(confirmButton(root)).not.toBeNull();
    expect(confirmButton(root)!.disabled).toBe(false);
    expect(root.querySelector('[data-testid="enable-collection-error"]')).toBeNull();
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
    // The preview — the one body a confirm would append — is shown only in this state.
    expect(root.querySelector('[data-testid="enable-collection-policy-preview"]')).not.toBeNull();
    expect(createPolicyRevision).not.toHaveBeenCalled();
  });

  it('disables the confirm and names the unreadable state on 403', async () => {
    readPolicyActivation.mockRejectedValue(failure('forbidden', 403));
    const fixture = await createPage();
    const root = fixture.nativeElement as HTMLElement;

    // The read-only preview stays: the user still sees what the step would do…
    expect(root.querySelector('[data-testid="enable-collection-policy-preview"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
    // …but the confirm is natively disabled, with the unreadable state named…
    expect(confirmButton(root)!.disabled).toBe(true);
    expect(text(root, '[data-testid="enable-collection-unreadable"]')).toContain(
      'could not be read with your permissions',
    );
    // …and described by both the warning and that reason.
    expect(confirmButton(root)!.getAttribute('aria-describedby')).toBe(
      'enable-replaces-warning enable-activation-unreadable',
    );
    // Never a live confirm: a refused read is not "no active policy".
    submit(root);
    await drain(fixture);
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
    expect(root.querySelector('[data-testid="enable-collection-error"]')).toBeNull();
  });

  it('names the missing grant and the unreadable state together when both apply', async () => {
    permitted.set(false);
    readPolicyActivation.mockRejectedValue(failure('forbidden', 403));
    const fixture = await createPage();
    const root = fixture.nativeElement as HTMLElement;

    expect(confirmButton(root)!.disabled).toBe(true);
    expect(confirmButton(root)!.getAttribute('aria-describedby')).toBe(
      'enable-replaces-warning enable-permission enable-activation-unreadable',
    );
    expect(text(root, '[data-testid="enable-collection-ask-approver"]')).toContain(
      'Ask your release approver',
    );
    expect(root.querySelector('[data-testid="enable-collection-unreadable"]')).not.toBeNull();
  });

  it('renders the shared error state and re-reads on retry for a 503', async () => {
    readPolicyActivation
      .mockRejectedValueOnce(failure('unavailable', 503, { retryable: true }))
      .mockResolvedValueOnce(null);
    const fixture = await createPage();
    const root = fixture.nativeElement as HTMLElement;

    const error = root.querySelector('[data-testid="enable-collection-error"]');
    expect(error).not.toBeNull();
    expect(error!.textContent).toContain('Exact server refusal for unavailable.');
    // No confirm while the read has not answered "none".
    expect(confirmButton(root)).toBeNull();
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();

    const retry = retryButton(root);
    expect(retry).not.toBeNull();
    expect(retry!.disabled).toBe(false);
    // The shared action already re-reads, so the page does not add a second way back.
    expect(readRetryAction(root)).toBeNull();
    retry!.click();
    await drain(fixture);

    expect(readPolicyActivation).toHaveBeenCalledTimes(2);
    expect(root.querySelector('[data-testid="enable-collection-error"]')).toBeNull();
    expect(confirmButton(root)).not.toBeNull();
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
  });

  it('returns focus to the named failure heading after each failed re-read', async () => {
    // Two consecutive failures of the same read: the control the user pressed is replaced by the
    // answer, so the page must hand focus to its own named target instead of dropping it on <body>.
    readPolicyActivation.mockRejectedValue(failure('unavailable', 503));
    const fixture = await createPage();
    const root = fixture.nativeElement as HTMLElement;

    // The first read's own failure takes no focus: nothing was pressed, so nothing moves.
    const firstHeading = root.querySelector('#enable-read-failed-heading');
    expect(firstHeading).not.toBeNull();
    expect(document.activeElement, 'the first failure took focus').not.toBe(firstHeading);
    expect(root.contains(document.activeElement), 'the page took focus on its first render').toBe(
      false,
    );

    for (const attempt of [1, 2]) {
      retryButton(root)!.click();
      await drain(fixture);
      const heading = root.querySelector<HTMLElement>('#enable-read-failed-heading');
      expect(heading, `failure ${attempt} did not render its heading`).not.toBeNull();
      // The target is the same named element on both failures — not <body>, and not the destroyed
      // control. `tabindex="-1"` keeps it out of the tab order: an announcement target, not a stop.
      expect(document.activeElement, `failure ${attempt} dropped focus`).toBe(heading);
      expect(heading!.getAttribute('tabindex')).toBe('-1');
      // The shared pattern's title drops to level 4 inside the named section, one outline level.
      expect(heading!.tagName).toBe('H3');
      expect(
        root.querySelector('app-error-pattern h4'),
        `failure ${attempt} rendered two headings at the same level`,
      ).not.toBeNull();
    }
    expect(readPolicyActivation).toHaveBeenCalledTimes(3);
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
  });
});
