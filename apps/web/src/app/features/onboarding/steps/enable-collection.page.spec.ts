// The step-5 page over a mocked data service.
//
// What each case here is allowed to prove, and what it is not:
// - The page derives "already enabled" from ONE server read (`GET .../policy-activation`):
//   200 restores the terminal panel without a command, 404 (the data service's `null`) is the only
//   state that offers the confirm, 403 is a read-only preview with a disabled confirm, and every
//   other failure is the shared error panel. A failed read is never treated as "no active policy".
// - "One audited `policy_activation` row" is the SERVER's half: the page can only prove it sends one
//   chain, one stable `Idempotency-Key` and the identical policy body, which is what the contract's
//   replay rule needs. The audit-cardinality assertion lives in
//   `tests/Tenancy.IntegrationTests/PolicyActivationTests.cs`, not in a component spec.
// - The disabled state is about a missing `policy:write` (a `release_approver` reassigned away),
//   not about a server refusal: a refusal is rendered as the typed error state instead.
import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type {
  PolicyActivationDto,
  PolicyActivationStateDto,
  PolicyRevisionDto,
} from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { EnableCollectionPage } from './enable-collection.page';
import { DEFAULT_ADVISORY_POLICY } from './default-policy';

const REVISION_HASH = 'sha256:9f2c1d7b4a6e8f0a1b2c3d4e5f60718293a4b5c6d7e8f9012345678a9b';

const REVISION: PolicyRevisionDto = {
  id: '018f0000-0000-7000-8000-00000000a004',
  project_id: 'project-1',
  revision_no: 1,
  hash: REVISION_HASH,
  state: 'draft',
  body: DEFAULT_ADVISORY_POLICY,
  created_by: 'user-1',
  created_at: '2026-10-27T00:00:00Z',
  version: 1,
};

const ACTIVATION: PolicyActivationDto = {
  project_id: 'project-1',
  policy_revision_id: REVISION.id,
  hash: REVISION_HASH,
  mode: 'advisory',
  activated_by: 'user-1',
  activated_at: '2026-10-27T01:00:00Z',
  previous_policy_revision_id: null,
  changed: true,
};

/** The reload/repeat answer when the revision is already the active one. */
const ACTIVATION_UNCHANGED: PolicyActivationDto = {
  ...ACTIVATION,
  previous_policy_revision_id: null,
  changed: false,
};

/** What the activation read answers for a project with an active policy. */
const ACTIVATION_STATE: PolicyActivationStateDto = {
  project_id: 'project-1',
  policy_revision_id: REVISION.id,
  hash: REVISION_HASH,
  mode: 'advisory',
  activated_by: 'user-1',
  activated_at: '2026-10-27T01:00:00Z',
};

/** The same read answering `enforcing`: the page must report the wire value, not advisory copy. */
const ACTIVATION_STATE_ENFORCING: PolicyActivationStateDto = {
  ...ACTIVATION_STATE,
  mode: 'enforcing',
};

/**
 * The contract signature of `OnboardingDataService.readPolicyActivation` (slice A adds it): a DTO on
 * 200, `null` on 404 only, and the gateway's typed rejection unchanged for everything else.
 */
type ReadActivation = (projectId: string) => Promise<PolicyActivationStateDto | null>;

/**
 * A promise whose settlement this spec controls. `Promise.withResolvers` is not in the web
 * project's `lib` target, so the executor is spelled out here once instead of at each use.
 */
function deferredResult<T>(): {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
} {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

/**
 * The statuses `mapApiError` marks retryable when the answer carries no envelope — the backoff pair
 * (429/503) and the transient gateway pair (502/504); `core/http/error-mapper.ts`.
 * A spec must not build an error shape the client cannot produce, so the helper derives the same
 * default and a caller overrides it only to pin the envelope's own `retryable` field.
 */
const RETRYABLE_STATUSES: readonly number[] = [429, 502, 503, 504];

/** One typed refusal, shaped as `mapApiError` would build it. */
function failure(
  kind: ApiError['kind'],
  status: number,
  options: {
    retryable?: boolean;
    retryAfterSeconds?: number | null;
    details?: ApiError['details'];
  } = {},
): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: kind,
    message: `Exact server refusal for ${kind}.`,
    requestId: 'request-1',
    retryable: options.retryable ?? RETRYABLE_STATUSES.includes(status),
    details: options.details ?? [],
    retryAfterSeconds: options.retryAfterSeconds ?? null,
  });
}

/**
 * Let the chain the page awaits run to its request. `whenStable` covers the framework's own work;
 * the macrotask turn before it drains the mocked promise the page awaits between the append and
 * the activation, so an assertion never reads a half-run chain.
 */
async function drain(target: ComponentFixture<unknown>): Promise<void> {
  await new Promise<void>((resolve) => {
    setTimeout(resolve, 0);
  });
  await target.whenStable();
}

function confirmButton(root: HTMLElement): HTMLButtonElement | null {
  return root.querySelector<HTMLButtonElement>('[data-testid="enable-collection-confirm"]');
}

function retryButton(root: HTMLElement): HTMLButtonElement | null {
  return root.querySelector<HTMLButtonElement>('app-error-pattern button');
}

/** The page-owned re-read action, used when the shared pattern offers no Retry. */
function readRetryAction(root: HTMLElement): HTMLButtonElement | null {
  return root.querySelector<HTMLButtonElement>('[data-testid="enable-collection-retry-read"]');
}

/** The one recovery a refused (403) read offers. */
function readAgainButton(root: HTMLElement): HTMLButtonElement | null {
  return root.querySelector<HTMLButtonElement>('[data-testid="enable-collection-read-again"]');
}

function text(root: HTMLElement, selector: string): string {
  return root.querySelector(selector)?.textContent?.trim() ?? '';
}

/** The form's submit event — what the confirm control itself sends. */
function submit(root: HTMLElement): void {
  const form = root.querySelector('form');
  form?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
}

describe('EnableCollectionPage', () => {
  let fixture: ComponentFixture<EnableCollectionPage>;
  let root: HTMLElement;
  const permitted = signal(true);
  /** The session's grant check: true only for the exact code the step names (`policy:write`). */
  const has = vi.fn((code: string) => permitted() && code === 'policy:write');
  const createPolicyRevision = vi.fn<OnboardingDataService['createPolicyRevision']>();
  const activatePolicy = vi.fn<OnboardingDataService['activatePolicy']>();
  const readPolicyActivation = vi.fn<ReadActivation>();

  function settle(): Promise<void> {
    return drain(fixture);
  }

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
    // The call's own tuple is typed, so the arguments are read instead of matched with `expect.any`
    // (an `any` the lint config refuses): one call, the wizard's project, the appended revision, and
    // a minted non-empty key — the same key the retry case below pins.
    expect(activatePolicy).toHaveBeenCalledTimes(1);
    const [calledProject, calledRevision, calledIntent] = activatePolicy.mock.calls[0]!;
    expect(calledProject).toBe('project-1');
    expect(calledRevision).toBe(REVISION.id);
    expect(typeof calledIntent.idempotencyKey).toBe('string');
    expect(calledIntent.idempotencyKey).not.toBe('');

    const enabled = root.querySelector('[data-testid="enable-collection-enabled"]');
    expect(enabled).not.toBeNull();
    // The confirm is replaced by the terminal panel, never shown beside it as still pending.
    expect(confirmButton(root)).toBeNull();
    expect(enabled!.textContent).toContain('Project enabled and collecting');
    expect(enabled!.textContent).toContain('advisory mode');
    expect(enabled!.textContent).toContain('Advisory');
    // Revision number comes from the appended revision, the hash is abbreviated, and the instant is
    // rendered from the answer as a machine-readable time element.
    const values = Array.from(enabled!.querySelectorAll('dd')).map((dd) => dd.textContent?.trim());
    expect(values).toContain('1');
    expect(enabled!.textContent).toContain('sha256:9f2c1d7b4a6e…');
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
    // The identical body is what the server replays against, on both attempts.
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
        expect(error!.textContent).toContain('/mode');
        expect(error!.textContent).toContain('invalid_value');
      }
      // None of these is retryable in the contract, so no retry action is offered.
      expect(retryButton(root)).toBeNull();
    },
  );

  it('never renders bidi or control characters a server label carries', async () => {
    // U+202E (right-to-left override) and U+009B (C1 control) inside the digest, i.e. inside the
    // characters the abbreviation shows — a sanitised-after-abbreviation bug would surface here.
    const hostileHash = 'sha256:9f2c\u202E\u009B1d7b4a6e8f0a';
    activatePolicy.mockResolvedValue({ ...ACTIVATION, hash: hostileHash });
    submit(root);
    await settle();

    const enabled = root.querySelector('[data-testid="enable-collection-enabled"]')!;
    expect(enabled.textContent).not.toContain('\u202E');
    expect(enabled.textContent).not.toContain('\u009B');
    expect(enabled.textContent).toContain('sha256:9f2c1d7b4a6e…');
    // The label is still interpolated text inside `<bdi>`, never markup.
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

describe('EnableCollectionPage activation read', () => {
  const permitted = signal(true);
  const has = vi.fn((code: string) => permitted() && code === 'policy:write');
  const createPolicyRevision = vi.fn<OnboardingDataService['createPolicyRevision']>();
  const activatePolicy = vi.fn<OnboardingDataService['activatePolicy']>();
  const readPolicyActivation = vi.fn<ReadActivation>();

  beforeEach(() => {
    permitted.set(true);
    createPolicyRevision.mockReset().mockResolvedValue(REVISION);
    activatePolicy.mockReset().mockResolvedValue(ACTIVATION);
    readPolicyActivation.mockReset();
  });

  /** The read is configured BEFORE the page exists: its constructor effect issues it. */
  async function createPage(): Promise<ComponentFixture<EnableCollectionPage>> {
    await TestBed.configureTestingModule({
      imports: [EnableCollectionPage],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: OnboardingDataService,
          useValue: { createPolicyRevision, activatePolicy, readPolicyActivation },
        },
        { provide: SessionStore, useValue: { has } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(EnableCollectionPage);
    fixture.componentRef.setInput('orgId', 'org-1');
    fixture.componentRef.setInput('projectId', 'project-1');
    await drain(fixture);
    return fixture;
  }

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

@Component({
  selector: 'app-enable-collection-host',
  imports: [EnableCollectionPage],
  template: `<app-onboarding-enable-collection [orgId]="orgId()" [projectId]="projectId()" />`,
})
class EnableCollectionHost {
  readonly orgId = signal('org-1');
  readonly projectId = signal('project-1');
}

describe('EnableCollectionPage scope changes', () => {
  const permitted = signal(true);
  const has = vi.fn((code: string) => permitted() && code === 'policy:write');
  const createPolicyRevision = vi.fn<OnboardingDataService['createPolicyRevision']>();
  const activatePolicy = vi.fn<OnboardingDataService['activatePolicy']>();
  const readPolicyActivation = vi.fn<ReadActivation>();
  let host: ComponentFixture<EnableCollectionHost>;
  let hostRoot: HTMLElement;

  function press(): void {
    submit(hostRoot);
  }

  async function drainHost(): Promise<void> {
    await drain(host);
  }

  beforeEach(async () => {
    permitted.set(true);
    createPolicyRevision.mockReset().mockResolvedValue(REVISION);
    activatePolicy.mockReset().mockResolvedValue(ACTIVATION);
    readPolicyActivation.mockReset().mockResolvedValue(null);
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [EnableCollectionHost],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: OnboardingDataService,
          useValue: { createPolicyRevision, activatePolicy, readPolicyActivation },
        },
        { provide: SessionStore, useValue: { has } },
      ],
    }).compileComponents();
    host = TestBed.createComponent(EnableCollectionHost);
    hostRoot = host.nativeElement as HTMLElement;
    await drain(host);
  });

  it('clears the appended revision and the intent when the scope moves', async () => {
    press();
    await drainHost();
    expect(hostRoot.querySelector('[data-testid="enable-collection-enabled"]')).not.toBeNull();
    const firstKey = activatePolicy.mock.calls[0]![2].idempotencyKey;

    // The wizard moved to another project: the terminal panel describes an answer for the old one.
    host.componentInstance.projectId.set('project-2');
    await drainHost();
    expect(hostRoot.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
    expect(confirmButton(hostRoot)).not.toBeNull();

    press();
    await drainHost();
    // A new intent for the new scope: new key, and the cached revision of the old intent is gone.
    expect(createPolicyRevision).toHaveBeenCalledTimes(2);
    expect(createPolicyRevision).toHaveBeenLastCalledWith('project-2', DEFAULT_ADVISORY_POLICY);
    expect(activatePolicy).toHaveBeenCalledTimes(2);
    expect(activatePolicy.mock.calls[1]![2].idempotencyKey).not.toBe(firstKey);
  });

  it('re-reads the activation when the scope moves and shows what the new read answers', async () => {
    // The first read (the host's initial project) already answered 404 during setup.
    readPolicyActivation
      .mockReset()
      .mockResolvedValue({ ...ACTIVATION_STATE, project_id: 'project-2' });
    host.componentInstance.projectId.set('project-2');
    await drainHost();

    expect(readPolicyActivation).toHaveBeenCalledExactlyOnceWith('project-2');
    const enabled = hostRoot.querySelector('[data-testid="enable-collection-enabled"]');
    expect(enabled).not.toBeNull();
    expect(enabled!.textContent).toContain('project-2');
    // The new scope's activation is read-only: no confirm and no command.
    expect(confirmButton(hostRoot)).toBeNull();
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
  });

  it('re-reads on an org change and ignores the late answer of the previous scope', async () => {
    // The setup read for org-1 already answered 404; this test owns the next two reads.
    const deferred = deferredResult<PolicyActivationStateDto | null>();
    readPolicyActivation
      .mockReset()
      .mockImplementationOnce(() => deferred.promise)
      .mockResolvedValueOnce(null);

    // The org moved while this read was in flight; the project id did not.
    host.componentInstance.orgId.set('org-2');
    await drainHost();
    expect(readPolicyActivation).toHaveBeenCalledTimes(1);

    // …and it moved again, so the answer deferred for org-2 is stale by the time it arrives.
    host.componentInstance.orgId.set('org-3');
    await drainHost();
    expect(readPolicyActivation).toHaveBeenCalledTimes(2);
    expect(readPolicyActivation).toHaveBeenLastCalledWith('project-1');

    deferred.resolve(ACTIVATION_STATE);
    await drainHost();

    // The stale answer belongs to org-1 and must not repaint this page.
    expect(hostRoot.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
    expect(confirmButton(hostRoot)).not.toBeNull();
    expect(confirmButton(hostRoot)!.disabled).toBe(false);
  });

  it('drops the late answer of the project a project change left behind', async () => {
    // The setup read for project-1 already answered 404; this test owns the next two reads.
    const deferred = deferredResult<PolicyActivationStateDto | null>();
    readPolicyActivation
      .mockReset()
      .mockResolvedValue(null)
      .mockImplementationOnce(() => deferred.promise)
      .mockResolvedValueOnce({ ...ACTIVATION_STATE, project_id: 'project-2' });

    // An org move starts a read for project-1 that is still in flight when the project moves.
    host.componentInstance.orgId.set('org-2');
    await drainHost();
    expect(readPolicyActivation).toHaveBeenCalledTimes(1);
    expect(readPolicyActivation).toHaveBeenLastCalledWith('project-1');

    host.componentInstance.projectId.set('project-2');
    await drainHost();
    expect(readPolicyActivation).toHaveBeenCalledTimes(2);
    expect(readPolicyActivation).toHaveBeenLastCalledWith('project-2');

    // The deferred answer belongs to project-1, and the page now shows project-2's.
    deferred.resolve(ACTIVATION_STATE);
    await drainHost();

    const enabled = hostRoot.querySelector('[data-testid="enable-collection-enabled"]');
    expect(enabled).not.toBeNull();
    expect(enabled!.textContent).toContain('project-2');
    expect(enabled!.textContent).not.toContain('project-1');
    expect(confirmButton(hostRoot)).toBeNull();
  });

  it('keeps a lost re-read’s focus request out of the next scope’s failure', async () => {
    // A read the user pressed Retry for, left in flight while the scope moves: its focus request
    // belongs to a press that no longer exists, so the new scope's *first* failure — an answer to
    // nobody's press — must leave focus where it is.
    readPolicyActivation.mockReset().mockRejectedValue(failure('unavailable', 503));
    host.componentInstance.projectId.set('project-2');
    await drainHost();
    const heading = hostRoot.querySelector<HTMLElement>('#enable-read-failed-heading');
    expect(heading).not.toBeNull();
    expect(document.activeElement).not.toBe(heading);

    const inFlight = deferredResult<PolicyActivationStateDto | null>();
    readPolicyActivation
      .mockReset()
      .mockResolvedValue(null)
      .mockImplementationOnce(() => inFlight.promise)
      .mockRejectedValueOnce(failure('unavailable', 503));

    retryButton(hostRoot)!.click();
    await drainHost();
    expect(readPolicyActivation).toHaveBeenCalledExactlyOnceWith('project-2');

    // The scope moves before that answer arrives; the new scope's own read fails, and only then does
    // the answer the lost generation was waiting for arrive.
    host.componentInstance.projectId.set('project-3');
    await drainHost();
    expect(readPolicyActivation).toHaveBeenLastCalledWith('project-3');
    inFlight.resolve(null);
    await drainHost();

    const latest = hostRoot.querySelector<HTMLElement>('#enable-read-failed-heading');
    expect(latest).not.toBeNull();
    expect(document.activeElement).not.toBe(latest);
    expect(
      hostRoot.contains(document.activeElement),
      'a failure nobody pressed for took focus',
    ).toBe(false);
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
  });
});
