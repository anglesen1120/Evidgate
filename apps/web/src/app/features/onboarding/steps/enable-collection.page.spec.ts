import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { PolicyActivationDto, PolicyRevisionDto } from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { provideDesktopOnboardingViewport } from '../onboarding-viewport.testing';
import { DEFAULT_ADVISORY_POLICY } from './default-policy';
import { EnableCollectionPage } from './enable-collection.page';
import {
  ACTIVATION,
  ACTIVATION_UNCHANGED,
  REVISION,
  type ReadActivation,
  confirmButton,
  deferredResult,
  drain,
  failure,
  retryButton,
  submit,
  text,
} from './enable-collection.page.test-support';

describe('EnableCollectionPage', () => {
  let fixture: ComponentFixture<EnableCollectionPage>;
  let root: HTMLElement;
  const permitted = signal(true);
  const has = vi.fn((code: string) => permitted() && code === 'policy:write');
  const createPolicyRevision = vi.fn<OnboardingDataService['createPolicyRevision']>();
  const activatePolicy = vi.fn<OnboardingDataService['activatePolicy']>();
  const readPolicyActivation = vi.fn<ReadActivation>();
  const settle = (): Promise<void> => drain(fixture);

  beforeEach(async () => {
    permitted.set(true);
    has.mockClear();
    createPolicyRevision.mockReset().mockResolvedValue(REVISION);
    activatePolicy.mockReset().mockResolvedValue(ACTIVATION);
    // 404: the project has no activation, which is the only state that offers the confirm.
    readPolicyActivation.mockReset().mockResolvedValue(null);
    await TestBed.configureTestingModule({
      imports: [EnableCollectionPage],
      providers: [
        provideZonelessChangeDetection(),
        provideDesktopOnboardingViewport(),
        {
          provide: OnboardingDataService,
          useValue: { createPolicyRevision, activatePolicy, readPolicyActivation },
        },
        { provide: SessionStore, useValue: { has } },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(EnableCollectionPage);
    fixture.componentRef.setInput('orgId', 'org-1');
    fixture.componentRef.setInput('projectId', 'project-1');
    root = fixture.nativeElement as HTMLElement;
    await drain(fixture);
  });

  it('previews the default advisory policy, states nothing is gated yet, and names the hazard', () => {
    expect(readPolicyActivation).toHaveBeenCalledExactlyOnceWith('project-1');
    const preview = text(root, '[data-testid="enable-collection-policy-preview"]');
    // The 24 h late-evidence window, in minutes, as the body carries it.
    expect(preview).toContain('1440');
    expect(preview).toContain('Not gating yet');
    expect(preview).toContain('advisory mode');
    // The preview is the constant body: nothing is required of a project that configured nothing.
    expect(preview).toContain('Required suites');
    expect(preview).toContain('None');
    expect(text(root, '[data-testid="enable-collection-not-gating"]')).toContain(
      'never blocks a merge',
    );
    // The lone creator holds `policy:write`…
    expect(has).toHaveBeenCalledWith('policy:write');
    expect(root.querySelector('[data-testid="enable-collection-ask-approver"]')).toBeNull();
    // …and the read answered "no activation", so the confirm is offered at once: no acknowledgement
    // stands between the user and the action any more.
    expect(confirmButton(root)!.disabled).toBe(false);
    expect(root.querySelector('[data-testid="enable-collection-acknowledge"]')).toBeNull();
    expect(root.querySelector('input[type="checkbox"]')).toBeNull();
    // The replace warning is stated before the press and is part of the control's description.
    expect(text(root, '[data-testid="enable-collection-replaces-active"]')).toContain(
      'activates the default advisory policy for this project',
    );
    expect(text(root, '[data-testid="enable-collection-replaces-active"]')).toContain(
      'only replace a policy activated after this page read',
    );
    expect(confirmButton(root)!.getAttribute('aria-describedby')).toBe('enable-replaces-warning');
    // Nothing mutating happened on a pure page load.
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
  });

  it('appends the exact default body once, then activates it in advisory mode, and shows the terminal panel', async () => {
    submit(root);
    await settle();

    expect(createPolicyRevision).toHaveBeenCalledExactlyOnceWith(
      'project-1',
      DEFAULT_ADVISORY_POLICY,
    );
    // Inspect the typed tuple without introducing an untyped matcher.
    expect(activatePolicy).toHaveBeenCalledTimes(1);
    const [calledProject, calledRevision, calledIntent] = activatePolicy.mock.calls[0]!;
    expect(calledProject).toBe('project-1');
    expect(calledRevision).toBe(REVISION.id);
    expect(typeof calledIntent.idempotencyKey).toBe('string');
    expect(calledIntent.idempotencyKey).not.toBe('');

    const enabled = root.querySelector('[data-testid="enable-collection-enabled"]');
    expect(enabled).not.toBeNull();
    expect(confirmButton(root)).toBeNull();
    expect(enabled!.textContent).toContain('Project enabled and collecting');
    expect(enabled!.textContent).toContain('advisory mode');
    expect(enabled!.textContent).toContain('Advisory');
    // Revision and full hash come from the answer; the instant stays machine-readable.
    const values = Array.from(enabled!.querySelectorAll('dd')).map((dd) => dd.textContent?.trim());
    expect(values).toContain('1');
    expect(enabled!.querySelector('[data-testid="enable-policy-hash"]')?.textContent?.trim()).toBe(
      ACTIVATION.hash,
    );
    expect(enabled!.querySelector('time')?.getAttribute('datetime')).toBe(ACTIVATION.activated_at);
    // No verdict language anywhere: step 5 reports collection, not a release decision (FR-09-1).
    expect(root.textContent).not.toMatch(/\bPASS\b|\bFAIL\b/);
  });

  it('treats a replayed answer (changed: false) as a confirmed activation (reload repeat)', async () => {
    activatePolicy.mockResolvedValue(ACTIVATION_UNCHANGED);
    submit(root);
    await settle();

    const enabled = root.querySelector('[data-testid="enable-collection-enabled"]');
    expect(enabled).not.toBeNull();
    // The copy says what happened, without claiming a second activation was written.
    expect(enabled!.textContent).toContain('changed nothing');
    expect(root.querySelector('[data-testid="enable-collection-error"]')).toBeNull();
  });

  it('disables the confirm and asks for a release approver when the user lacks policy:write', async () => {
    permitted.set(false);
    await settle();

    expect(confirmButton(root)!.disabled).toBe(true);
    expect(confirmButton(root)!.getAttribute('aria-describedby')).toContain('enable-permission');
    expect(text(root, '[data-testid="enable-collection-ask-approver"]')).toContain(
      'Ask your release approver',
    );
    submit(root);
    await settle();
    // Disabled is not decoration: neither call leaves the page.
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
  });

  it('sends nothing until the confirm is pressed', async () => {
    await settle();
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();

    submit(root);
    await settle();
    expect(createPolicyRevision).toHaveBeenCalledTimes(1);
    expect(activatePolicy).toHaveBeenCalledTimes(1);
  });

  it('sends one intent for two synchronous presses and one stable activation key', async () => {
    const deferred = deferredResult<PolicyActivationDto>();
    activatePolicy.mockImplementationOnce(() => deferred.promise);

    // Both submits run in the same task, before any await could settle the first chain.
    submit(root);
    submit(root);
    await settle();
    expect(createPolicyRevision).toHaveBeenCalledTimes(1);
    expect(activatePolicy).toHaveBeenCalledTimes(1);

    const key = activatePolicy.mock.calls[0]![2].idempotencyKey;
    deferred.resolve(ACTIVATION);
    await settle();
    expect(activatePolicy.mock.calls[0]![2].idempotencyKey).toBe(key);
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).not.toBeNull();
    // The double press opened exactly one chain, so a third press after success is irrelevant:
    // the key from the first press was the only one sent.
    expect(activatePolicy.mock.calls.map(([, , intent]) => intent.idempotencyKey)).toEqual([key]);
  });

  it('retries an uncertain activation with the same key, without appending again, and only on a press', async () => {
    activatePolicy.mockRejectedValueOnce(failure('timeout', 0, { retryable: true }));
    submit(root);
    await settle();

    // A transport failure claims no outcome: the panel says so and offers the same-intent retry.
    expect(root.querySelector('[data-testid="enable-collection-error"]')).not.toBeNull();
    expect(text(root, '[data-testid="enable-collection-response-uncertain"]')).toContain(
      'may already be recorded',
    );
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
    const retry = retryButton(root);
    expect(retry).not.toBeNull();
    expect(retry!.disabled).toBe(false);

    // Nothing is re-sent by the failure itself: the page waits for a deliberate press.
    await settle();
    expect(createPolicyRevision).toHaveBeenCalledTimes(1);
    expect(activatePolicy).toHaveBeenCalledTimes(1);

    retry!.click();
    await settle();

    expect(createPolicyRevision).toHaveBeenCalledTimes(1);
    expect(activatePolicy).toHaveBeenCalledTimes(2);
    const [first, second] = activatePolicy.mock.calls.map(([, , intent]) => intent);
    expect(second!.idempotencyKey).toBe(first!.idempotencyKey);
    expect(createPolicyRevision.mock.calls.map(([, body]) => body)).toEqual([
      DEFAULT_ADVISORY_POLICY,
    ]);
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).not.toBeNull();
  });

  it('names a refused confirm, announces its field rows once, and takes focus alone', async () => {
    activatePolicy.mockRejectedValueOnce(
      failure('validation', 422, { details: [{ path: '/mode', reason: 'invalid_value' }] }),
    );
    submit(root);
    await settle();

    // The section takes its name from whichever page-owned heading this branch rendered.
    const section = root.querySelector('[data-testid="enable-collection-error"]')!;
    expect(section.getAttribute('aria-labelledby')).toBe('enable-confirm-failed-heading');
    const heading = root.querySelector<HTMLElement>('#enable-confirm-failed-heading');
    expect(heading).not.toBeNull();
    expect(heading!.tagName).toBe('H3');
    expect(root.querySelector('#enable-read-failed-heading')).toBeNull();
    // The press disabled the confirm, so the place goes to that heading — never into an alert.
    expect(document.activeElement).toBe(heading);
    expect(heading!.closest('[role="alert"]')).toBeNull();

    // The rows the envelope carried are spoken from one labelled alert, sanitised as text.
    const details = root.querySelector<HTMLElement>('[data-testid="enable-collection-details"]');
    expect(details).not.toBeNull();
    expect(details!.getAttribute('role')).toBe('alert');
    const label = root.querySelector(`#${details!.getAttribute('aria-labelledby')}`);
    expect(label?.textContent).toContain('The server refused');
    expect(details!.textContent).toContain('invalid_value');
    // The shared pattern's own title is one outline level below the page's heading.
    expect(root.querySelector('app-error-pattern h4')).not.toBeNull();
  });

  it.each([
    ['conflict', 409],
    ['validation', 422],
    ['forbidden', 403],
  ] as const)(
    'renders a %s refusal as the typed error and never the terminal panel',
    async (kind, status) => {
      activatePolicy.mockRejectedValue(
        failure(kind, status, {
          details: kind === 'validation' ? [{ path: '/mode', reason: 'invalid_value' }] : [],
        }),
      );
      submit(root);
      await settle();

      const error = root.querySelector('[data-testid="enable-collection-error"]');
      expect(error).not.toBeNull();
      expect(error!.textContent).toContain(`Exact server refusal for ${kind}.`);
      expect(root.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
      // A refusal is not a success with a warning, and the page does not claim it was enabled.
      expect(root.textContent).not.toContain('Project enabled and collecting');
      if (kind === 'validation') {
        expect(error!.textContent).toContain('Mode:');
        expect(error!.textContent).toContain('/mode');
        expect(error!.textContent).toContain('invalid_value');
      }
      // None of these is retryable in the contract, so no retry action is offered.
      expect(retryButton(root)).toBeNull();
    },
  );

  it('never renders bidi or control characters a server label carries', async () => {
    // Invisible characters are stripped from the complete provenance value before presentation.
    const hostileHash = 'sha256:9f2c\u202E\u009B1d7b4a6e8f0a';
    activatePolicy.mockResolvedValue({ ...ACTIVATION, hash: hostileHash });
    submit(root);
    await settle();

    const enabled = root.querySelector('[data-testid="enable-collection-enabled"]')!;
    expect(enabled.textContent).not.toContain('\u202E');
    expect(enabled.textContent).not.toContain('\u009B');
    expect(enabled.querySelector('[data-testid="enable-policy-hash"]')?.textContent?.trim()).toBe(
      'sha256:9f2c1d7b4a6e8f0a',
    );
    // Provenance stays interpolated text, never markup.
    expect(enabled.querySelector('code')?.innerHTML).not.toContain('&lt;');
  });

  it('says confirming activates the default policy and names the revision it replaced', async () => {
    // The hazard is stated before the press: a confirm does replace the project's policy pointer.
    const warning = text(root, '[data-testid="enable-collection-replaces-active"]');
    expect(warning).toContain('activates the default advisory policy');
    expect(warning).toContain('only replace a policy activated after this page read');

    activatePolicy.mockResolvedValue({
      ...ACTIVATION,
      previous_policy_revision_id: '018f0000-0000-7000-8000-00000000a003',
    });
    submit(root);
    await settle();

    const enabled = root.querySelector('[data-testid="enable-collection-enabled"]')!;
    expect(enabled.textContent).toContain('Revision this replaced');
    expect(enabled.textContent).toContain('018f0000-0000-7000-8000-00000000a003');
  });

  it('omits the replaced-revision row when the answer names none', async () => {
    submit(root);
    await settle();

    const enabled = root.querySelector('[data-testid="enable-collection-enabled"]')!;
    expect(enabled.textContent).not.toContain('Revision this replaced');
  });

  it('renders the shared "Unavailable" copy instead of throwing on a malformed answer', async () => {
    activatePolicy.mockResolvedValue({
      ...ACTIVATION,
      hash: '',
      activated_at: 'not-a-date',
    });
    submit(root);
    await settle();

    const enabled = root.querySelector('[data-testid="enable-collection-enabled"]')!;
    // `DatePipe` throws on an invalid date, so no `<time>` may be built from this answer.
    expect(enabled.querySelector('time')).toBeNull();
    expect(enabled.textContent).toContain('Unavailable');
  });

  it('sanitises the refusal detail rows it renders itself', async () => {
    activatePolicy.mockRejectedValue(
      failure('validation', 422, {
        details: [{ path: '/late\u202Ewindow', reason: 'number_too_long\u200E' }],
      }),
    );
    submit(root);
    await settle();

    const error = root.querySelector('[data-testid="enable-collection-error"]')!;
    expect(error.textContent).not.toContain('\u202E');
    expect(error.textContent).not.toContain('\u200E');
    expect(error.textContent).toContain('Submitted value: number_too_long');
    expect(error.textContent).toContain('/latewindow');
  });

  it('disables the confirm while a retryable Retry-After window is still open', async () => {
    activatePolicy.mockRejectedValueOnce(
      new ApiRequestError({
        kind: 'rate_limited',
        status: 429,
        code: 'rate_limited',
        message: 'Too many requests.',
        requestId: 'request-1',
        retryable: true,
        details: [],
        retryAfterSeconds: 30,
      }),
    );
    submit(root);
    await settle();

    // The runner owns the window: while it is open the action could only be a no-op, so the control
    // says so natively instead of inviting a press. (The window opening is the runner's own spec.)
    expect(confirmButton(root)!.disabled).toBe(true);
    expect(retryButton(root)?.disabled).toBe(true);
  });

  it('drops a confirm refusal whose panel a failed read took, so the read’s recovery moves no focus', async () => {
    // The page's own gates cannot produce this state (a press needs the read to have answered, and a
    // scope change aborts the intent before a read can fail), so the read state is written directly:
    // the request must not survive the panel it was queued for.
    let refuseActivation!: (reason: ApiRequestError) => void;
    const confirmation = new Promise<PolicyActivationDto>((_, reject) => {
      refuseActivation = reject;
    });
    activatePolicy.mockImplementationOnce(() => confirmation);
    activatePolicy.mockRejectedValue(failure('validation', 422));
    submit(root);
    await settle();
    expect(confirmButton(root)!.disabled).toBe(true);

    // The read fails while the chain still runs, so its panel replaces the refusal's.
    const readError = failure('unavailable', 503).error;
    fixture.componentInstance['activationRead'].set({ kind: 'error', error: readError });
    await settle();
    refuseActivation(failure('validation', 422));
    await settle();
    expect(root.querySelector('#enable-confirm-failed-heading')).toBeNull();
    expect(root.querySelector('#enable-read-failed-heading')).not.toBeNull();

    // The read recovers through its own Retry: the refusal's heading returns, and nothing moves.
    retryButton(root)!.click();
    await settle();
    const heading = root.querySelector<HTMLElement>('#enable-confirm-failed-heading');
    expect(heading).not.toBeNull();
    expect(document.activeElement, 'a refused confirm took focus off its own reply').not.toBe(
      heading,
    );

    // The positive control: a refusal the page still shows takes that heading.
    submit(root);
    await settle();
    expect(document.activeElement).toBe(root.querySelector('#enable-confirm-failed-heading'));
  });

  it('moves focus to the terminal heading when the confirm goes away', async () => {
    submit(root);
    await settle();

    const heading = root.querySelector('#enable-enabled-heading');
    expect(heading).not.toBeNull();
    expect(heading!.getAttribute('tabindex')).toBe('-1');
    expect(document.activeElement).toBe(heading);
  });

  it('re-appends after a failed append and keeps one activation key across attempts', async () => {
    createPolicyRevision.mockRejectedValueOnce(failure('timeout', 0, { retryable: true }));
    submit(root);
    await settle();
    expect(createPolicyRevision).toHaveBeenCalledTimes(1);
    expect(activatePolicy).not.toHaveBeenCalled();

    activatePolicy.mockRejectedValueOnce(failure('timeout', 0, { retryable: true }));
    retryButton(root)!.click();
    await settle();
    // The append is retried because this intent produced no revision yet.
    expect(createPolicyRevision).toHaveBeenCalledTimes(2);
    expect(activatePolicy).toHaveBeenCalledTimes(1);

    retryButton(root)!.click();
    await settle();
    // …and once it has, the retry reuses it instead of appending a third time.
    expect(createPolicyRevision).toHaveBeenCalledTimes(2);
    expect(activatePolicy).toHaveBeenCalledTimes(2);
    const keys = activatePolicy.mock.calls.map(([, , intent]) => intent.idempotencyKey);
    expect(keys[0]).toBe(keys[1]);
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).not.toBeNull();
  });

  it('mints a new key after a non-retryable refusal', async () => {
    activatePolicy.mockRejectedValueOnce(failure('conflict', 409));
    submit(root);
    await settle();
    expect(retryButton(root)).toBeNull();

    submit(root);
    await settle();

    expect(activatePolicy).toHaveBeenCalledTimes(2);
    // A new intent means a new key — and therefore a fresh append, whose body is still identical.
    expect(createPolicyRevision).toHaveBeenCalledTimes(2);
    const keys = activatePolicy.mock.calls.map(([, , intent]) => intent.idempotencyKey);
    expect(keys[0]).not.toBe(keys[1]);
  });

  it('retries a retryable failure through the main confirm button with the same key', async () => {
    activatePolicy.mockRejectedValueOnce(failure('timeout', 0, { retryable: true }));
    submit(root);
    await settle();

    // The main control, not the error panel's action: `run()` from a retryable failure keeps the key.
    submit(root);
    await settle();

    expect(createPolicyRevision).toHaveBeenCalledTimes(1);
    expect(activatePolicy).toHaveBeenCalledTimes(2);
    const keys = activatePolicy.mock.calls.map(([, , intent]) => intent.idempotencyKey);
    expect(keys[0]).toBe(keys[1]);
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).not.toBeNull();
  });

  it('sends no activation when the append settles after the step was left', async () => {
    const deferred = deferredResult<PolicyRevisionDto>();
    createPolicyRevision.mockImplementationOnce(() => deferred.promise);
    submit(root);
    await settle();
    expect(createPolicyRevision).toHaveBeenCalledTimes(1);

    fixture.destroy();
    deferred.resolve(REVISION);
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });

    // The half-written append is one immutable revision (content-idempotent); the command that would
    // change the project's policy pointer must never leave for a view the user has left.
    expect(activatePolicy).not.toHaveBeenCalled();
  });
});
