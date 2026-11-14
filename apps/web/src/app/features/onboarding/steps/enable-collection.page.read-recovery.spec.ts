import {
  confirmButton,
  createActivationReadContext,
  drain,
  failure,
  readAgainButton,
  readRetryAction,
  retryButton,
  submit,
  text,
} from './enable-collection.page.test-support';

describe('EnableCollectionPage activation read recovery', () => {
  const { createPolicyRevision, activatePolicy, readPolicyActivation, createPage } =
    createActivationReadContext();

  it('closes the read controls for the answer’s own interval and opens them when it elapses', async () => {
    // The page's own interval is faked, together with the clock it is measured against, and nothing
    // else: the framework's scheduling and the spec's own macrotask stay real, so the wait is
    // advanced instead of waited for without freezing the render.
    vi.useFakeTimers({
      toFake: ['setInterval', 'clearInterval', 'Date'],
      now: new Date('2026-10-28T00:00:00Z'),
    });
    try {
      readPolicyActivation
        .mockRejectedValueOnce(
          failure('rate_limited', 429, { retryable: true, retryAfterSeconds: 30 }),
        )
        .mockResolvedValueOnce(null);
      const fixture = await createPage();
      const root = fixture.nativeElement as HTMLElement;
      const wait = (): string => text(root, '[data-testid="enable-collection-read-wait"]');

      // The window is open: the Retry says so natively, and the wait names both numbers — the time
      // left and the interval the server supplied — with copy that is neutral about the reason (the
      // same wait is shown for any answer that names an interval, not only a `429`).
      expect(retryButton(root)!.disabled).toBe(true);
      expect(wait()).toContain('The server asked you to wait');
      expect(wait()).toContain('30 s left of the 30 s it named');
      expect(wait()).not.toContain('Rate limited');
      // The countdown is text, never a live region: the sheet asks for the interval, not a
      // per-second announcement.
      const waitNode = root.querySelector('[data-testid="enable-collection-read-wait"]')!;
      expect(waitNode.getAttribute('role')).toBeNull();
      expect(waitNode.getAttribute('aria-live')).toBeNull();

      // One second short of the interval: still closed, and the remainder has counted down.
      await vi.advanceTimersByTimeAsync(29_000);
      await drain(fixture);
      expect(retryButton(root)!.disabled).toBe(true);
      expect(wait()).toContain('1 s left of the 30 s it named');

      // The window elapses: the control opens, the wait goes, and the read is the only request.
      await vi.advanceTimersByTimeAsync(1_000);
      await drain(fixture);
      expect(retryButton(root)!.disabled).toBe(false);
      expect(root.querySelector('[data-testid="enable-collection-read-wait"]')).toBeNull();

      retryButton(root)!.click();
      await drain(fixture);
      expect(readPolicyActivation).toHaveBeenCalledTimes(2);
      expect(root.querySelector('[data-testid="enable-collection-error"]')).toBeNull();
      expect(confirmButton(root)).not.toBeNull();
      expect(createPolicyRevision).not.toHaveBeenCalled();
      expect(activatePolicy).not.toHaveBeenCalled();
    } finally {
      vi.useRealTimers();
    }
  });

  it('offers one read-only recovery on a refused (403) read, keyboard reachable', async () => {
    readPolicyActivation.mockRejectedValue(failure('forbidden', 403));
    const fixture = await createPage();
    const root = fixture.nativeElement as HTMLElement;

    const readAgain = readAgainButton(root);
    expect(readAgain, 'a refused read offered no way back').not.toBeNull();
    // A native button: in the tab order, activated by Enter/Space — never a `tabindex="-1"` or
    // `aria-disabled` imitation, and never a second control beside it.
    expect(readAgain!.tagName).toBe('BUTTON');
    expect(readAgain!.type).toBe('button');
    expect(readAgain!.disabled).toBe(false);
    expect(readAgain!.tabIndex).toBe(0);
    expect(readAgain!.hasAttribute('aria-disabled')).toBe(false);
    expect(root.querySelectorAll('button')).toHaveLength(2);

    // The refusal stays visible and the confirm stays natively disabled: nothing mutating is
    // reachable from this state.
    expect(text(root, '[data-testid="enable-collection-unreadable"]')).toContain(
      'could not be read with your permissions',
    );
    expect(confirmButton(root)!.disabled).toBe(true);

    // The control issues the read and nothing else — twice, so a repeated failure proves the focus
    // rule too: the pressed control is replaced, and focus returns to its successor.
    readAgain!.focus();
    expect(document.activeElement).toBe(readAgain);
    readAgain!.click();
    await drain(fixture);
    expect(readPolicyActivation).toHaveBeenCalledTimes(2);
    expect(document.activeElement).toBe(readAgainButton(root));

    readAgainButton(root)!.click();
    await drain(fixture);
    expect(readPolicyActivation).toHaveBeenCalledTimes(3);
    expect(document.activeElement).toBe(readAgainButton(root));
    // No command was ever sent, and the confirm is still closed.
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
    expect(confirmButton(root)!.disabled).toBe(true);
  });

  it('never opens a wait window for a refused read, whatever the answer carries', async () => {
    // A `403` is a refusal, not a throttled attempt: an answer that still carries a `Retry-After`
    // must not close the one recovery a refused read offers, or the page would strand the user for
    // an interval nobody asked them to wait.
    readPolicyActivation.mockRejectedValue(
      failure('forbidden', 403, { retryable: false, retryAfterSeconds: 30 }),
    );
    const fixture = await createPage();
    const root = fixture.nativeElement as HTMLElement;

    const readAgain = readAgainButton(root);
    expect(readAgain).not.toBeNull();
    expect(readAgain!.disabled).toBe(false);
    expect(root.querySelector('[data-testid="enable-collection-read-wait"]')).toBeNull();

    readAgain!.click();
    await drain(fixture);
    expect(readPolicyActivation).toHaveBeenCalledTimes(2);
    expect(readAgainButton(root)!.disabled).toBe(false);
    expect(root.querySelector('[data-testid="enable-collection-read-wait"]')).toBeNull();
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
  });

  it.each([
    // An absent or zero interval is no interval ([DECISION 2]): the Retry is usable at once, and an
    // interval the server did state closes it for exactly that long (the fake-clock case above).
    ['rate_limited', 429, null],
    ['unavailable', 503, null],
  ] as const)(
    're-reads a retryable %s read failure through the shared Retry action at once',
    async (kind, status, retryAfterSeconds) => {
      // The shape the client produces (`core/http/error-mapper.ts`): a 429 or 503 read is retryable,
      // and a 429 carries the `Retry-After` interval the answer named.
      readPolicyActivation
        .mockRejectedValueOnce(failure(kind, status, { retryable: true, retryAfterSeconds }))
        .mockResolvedValueOnce(null);
      const fixture = await createPage();
      const root = fixture.nativeElement as HTMLElement;

      expect(root.querySelector('[data-testid="enable-collection-error"]')).not.toBeNull();
      expect(confirmButton(root)).toBeNull();
      expect(root.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
      submit(root);
      await drain(fixture);
      expect(createPolicyRevision).not.toHaveBeenCalled();
      expect(activatePolicy).not.toHaveBeenCalled();

      // The shared pattern renders its Retry for a retryable failure, and it re-reads: the page adds
      // no second way back beside it.
      const retry = retryButton(root);
      expect(retry).not.toBeNull();
      expect(retry!.disabled).toBe(false);
      expect(readRetryAction(root)).toBeNull();

      retry!.click();
      await drain(fixture);
      expect(readPolicyActivation).toHaveBeenCalledTimes(2);
      expect(root.querySelector('[data-testid="enable-collection-error"]')).toBeNull();
      expect(confirmButton(root)).not.toBeNull();
    },
  );

  it.each([
    ['unauthenticated', 401],
    ['unexpected', 500],
  ] as const)(
    'never treats a non-retryable %s read failure as "no active policy"',
    async (kind, status) => {
      // A 401 and a 500 are not retryable in the contract, so the shared pattern renders no Retry and
      // the page's own re-read control is the action — still never read as "no active policy".
      readPolicyActivation
        .mockRejectedValueOnce(failure(kind, status, { retryable: false }))
        .mockResolvedValueOnce(null);
      const fixture = await createPage();
      const root = fixture.nativeElement as HTMLElement;

      expect(root.querySelector('[data-testid="enable-collection-error"]')).not.toBeNull();
      expect(confirmButton(root)).toBeNull();
      expect(root.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
      expect(retryButton(root)).toBeNull();
      submit(root);
      await drain(fixture);
      expect(createPolicyRevision).not.toHaveBeenCalled();
      expect(activatePolicy).not.toHaveBeenCalled();

      const reRead = readRetryAction(root);
      expect(reRead).not.toBeNull();
      expect(reRead!.disabled).toBe(false);

      reRead!.click();
      await drain(fixture);
      expect(readPolicyActivation).toHaveBeenCalledTimes(2);
      expect(root.querySelector('[data-testid="enable-collection-error"]')).toBeNull();
      expect(confirmButton(root)).not.toBeNull();
    },
  );

  it('renders a typed error instead of a stuck loading panel when the read throws a plain error', async () => {
    readPolicyActivation.mockRejectedValueOnce(new Error('boom')).mockResolvedValueOnce(null);
    const fixture = await createPage();
    const root = fixture.nativeElement as HTMLElement;

    // A programming error still lands in a rendered state with a way back — never a silent hang.
    const error = root.querySelector('[data-testid="enable-collection-error"]');
    expect(error).not.toBeNull();
    expect(error!.textContent).toContain('The activation could not be read.');
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
    expect(confirmButton(root)).toBeNull();

    const retry = retryButton(root);
    expect(retry).not.toBeNull();
    retry!.click();
    await drain(fixture);
    expect(readPolicyActivation).toHaveBeenCalledTimes(2);
    expect(root.querySelector('[data-testid="enable-collection-error"]')).toBeNull();
    expect(confirmButton(root)).not.toBeNull();
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
  });

  /**
   * A cancellation is not a scope change: nothing on this page aborts its own read, so one that
   * arrives belongs to the read in hand. Dropping it silently would leave the panel reading
   * forever, so it becomes the unexpected-error state — whatever class or realm the rejection
   * carries, which is why nothing here asks what the error is an instance of. The abort of a stale
   * scope never reaches this branch: the generation check returns for it first.
   */
  const cancellations: readonly (readonly [string, () => unknown])[] = [
    ['a DOM abort', () => new DOMException('The request was aborted.', 'AbortError')],
    [
      'a foreign-realm abort',
      () => Object.assign(new Error('The request was aborted.'), { name: 'AbortError' }),
    ],
  ];

  it.each(cancellations)(
    'renders the unexpected-error state when %s cancels a read this page still owns',
    async (_shape, cancellation) => {
      readPolicyActivation.mockRejectedValueOnce(cancellation()).mockResolvedValueOnce(null);
      const fixture = await createPage();
      const root = fixture.nativeElement as HTMLElement;

      const error = root.querySelector('[data-testid="enable-collection-error"]');
      expect(error).not.toBeNull();
      expect(error!.textContent).toContain('The activation could not be read.');
      expect(root.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
      expect(confirmButton(root)).toBeNull();
      // The unexpected failure is retryable: re-reading a read that wrote nothing is always safe.
      const retry = retryButton(root);
      expect(retry).not.toBeNull();
      retry!.click();
      await drain(fixture);
      expect(readPolicyActivation).toHaveBeenCalledTimes(2);
      expect(root.querySelector('[data-testid="enable-collection-error"]')).toBeNull();
      expect(confirmButton(root)).not.toBeNull();
      expect(createPolicyRevision).not.toHaveBeenCalled();
      expect(activatePolicy).not.toHaveBeenCalled();
    },
  );

  it('has no acknowledgement control in any read state', async () => {
    readPolicyActivation.mockResolvedValue(null);
    const fixture = await createPage();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="enable-collection-acknowledge"]')).toBeNull();
    expect(root.querySelector('input[type="checkbox"]')).toBeNull();
    expect(root.textContent).not.toContain('I understand this replaces the active policy');
    // The warning itself stays, and the control is described by it.
    expect(root.querySelector('#enable-replaces-warning')).not.toBeNull();
    expect(confirmButton(root)!.getAttribute('aria-describedby')).toContain(
      'enable-replaces-warning',
    );
    // With the grant in hand there is nothing to acknowledge before pressing.
    expect(confirmButton(root)!.disabled).toBe(false);
  });
});
