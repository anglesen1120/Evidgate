// 09.2.1-T5 (plan 09.2.1 §2 AC5, §8 rows T98 and 00 §9.1 idempotency, §9 test-plan rows for
// `enable-collection.page.spec.ts`, §10 T5.1/T5.2, F18): the step-5 page over a mocked data service.
//
// What each case here is allowed to prove, and what it is not:
// - The page derives "already enabled" from ONE server read (`GET .../policy-activation`, F18):
//   200 restores the terminal panel without a command, 404 (the data service's `null`) is the only
//   state that offers the confirm, 403 is a read-only preview with a disabled confirm, and every
//   other failure is the shared error panel. A failed read is never treated as "no active policy".
// - "One audited `policy_activation` row" (§9 line 124) is the SERVER's half: the page can only
//   prove it sends one chain, one stable `Idempotency-Key` and the identical policy body, which is
//   what the contract's replay rule needs. The audit-cardinality assertion lives in
//   `tests/Tenancy.IntegrationTests/PolicyActivationTests.cs`, not in a component spec.
// - The disabled state is about a missing `policy:write` (a `release_approver` reassigned away,
//   AC5/T98), not about a server refusal: a refusal is rendered as the typed error state instead.
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

/** The reload/repeat answer 07.1.1-T4 gives when the revision is already the active one. */
const ACTIVATION_UNCHANGED: PolicyActivationDto = {
  ...ACTIVATION,
  previous_policy_revision_id: null,
  changed: false,
};

/** What the F18 read answers for a project with an active policy (07.1.1 `GET` shape). */
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
function deferredResult<T>(): { readonly promise: Promise<T>; readonly resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

/** One typed refusal, shaped as `mapApiError` would build it. */
function failure(
  kind: ApiError['kind'],
  status: number,
  options: { retryable?: boolean; details?: ApiError['details'] } = {},
): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: kind,
    message: `Exact server refusal for ${kind}.`,
    requestId: 'request-1',
    retryable: options.retryable ?? false,
    details: options.details ?? [],
    retryAfterSeconds: null,
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
  /** The session's grant check: true only for the exact code the step names (AC5: `policy:write`). */
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

  it('previews the default advisory policy, states nothing is gated yet, and names the hazard (AC5, AC-F18-c)', async () => {
    expect(readPolicyActivation).toHaveBeenCalledExactlyOnceWith('project-1');
    const preview = text(root, '[data-testid="enable-collection-policy-preview"]');
    // T62: the 24 h late-evidence window, in minutes, as the body carries it.
    expect(preview).toContain('1440');
    expect(preview).toContain('Not gating yet');
    expect(preview).toContain('advisory mode');
    // The preview is the constant body: nothing is required of a project that configured nothing.
    expect(preview).toContain('Required suites');
    expect(preview).toContain('None');
    expect(text(root, '[data-testid="enable-collection-not-gating"]')).toContain(
      'never blocks a merge',
    );
    // The lone creator holds `policy:write` (T98)…
    expect(has).toHaveBeenCalledWith('policy:write');
    expect(root.querySelector('[data-testid="enable-collection-ask-approver"]')).toBeNull();
    // …and the read answered "no activation", so the confirm is offered at once: no acknowledgement
    // stands between the user and the action any more (F18 addendum 1).
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

  it('appends the exact default body once, then activates it in advisory mode, and shows the terminal panel (AC5)', async () => {
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

  it('disables the confirm and asks for a release approver when the user lacks policy:write (AC5/T98)', async () => {
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

  it('sends nothing until the confirm is pressed (AC-F18-b)', async () => {
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

  it('retries an uncertain activation with the same key, without appending again, and only on a press (00 §9.1, AC-F18-b)', async () => {
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

  it('never renders bidi or control characters a server label carries (T47 precedent)', async () => {
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

  it('says confirming activates the default policy and names the revision it replaced (F1)', async () => {
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

  it('renders the shared "Unavailable" copy instead of throwing on a malformed answer (F5)', async () => {
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

  it('sanitises the refusal detail rows it renders itself (R3)', async () => {
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

  it('disables the confirm while a retryable Retry-After window is still open (R5)', async () => {
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

  it('moves focus to the terminal heading when the confirm goes away (QA-01)', async () => {
    submit(root);
    await settle();

    const heading = root.querySelector('#enable-enabled-heading');
    expect(heading).not.toBeNull();
    expect(heading!.getAttribute('tabindex')).toBe('-1');
    expect(document.activeElement).toBe(heading);
  });

  it('re-appends after a failed append and keeps one activation key across attempts (R4)', async () => {
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

  it('mints a new key after a non-retryable refusal (R4)', async () => {
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

  it('retries a retryable failure through the main confirm button with the same key (R4)', async () => {
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

  it('sends no activation when the append settles after the step was left (F3)', async () => {
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

  it('restores the terminal panel from the read alone and sends no command (AC-T5.2-1)', async () => {
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

  it('renders an enforcing mode verbatim and never the advisory claim (AC-F18-a)', async () => {
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

  it('keeps the confirm disabled until the read settles (AC-T5.2-3)', async () => {
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

  it('offers the confirm when the read answers no activation (404) (AC-T5.2-3, AC-F18-b)', async () => {
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

  it('disables the confirm and names the unreadable state on 403 (AC-T5.2-3, AC-F18-c)', async () => {
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

  it('renders the shared error state and re-reads on retry for a 503 (AC-T5.2-3)', async () => {
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

  it.each([
    ['unauthenticated', 401],
    ['rate_limited', 429],
    ['unavailable', 503],
    ['unexpected', 500],
  ] as const)(
    'never treats a %s read failure as "no active policy" (AC-T5.2-3)',
    async (kind, status) => {
      readPolicyActivation
        .mockRejectedValueOnce(failure(kind, status))
        .mockResolvedValueOnce(null);
      const fixture = await createPage();
      const root = fixture.nativeElement as HTMLElement;

      expect(root.querySelector('[data-testid="enable-collection-error"]')).not.toBeNull();
      expect(confirmButton(root)).toBeNull();
      expect(root.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
      // The contract calls none of these retryable, so the shared action is absent and the page's own
      // re-read control is what offers the way back (AC-T5.2-3: a retry that re-reads).
      expect(retryButton(root)).toBeNull();
      const reRead = readRetryAction(root);
      expect(reRead).not.toBeNull();
      expect(reRead!.disabled).toBe(false);
      submit(root);
      await drain(fixture);
      expect(createPolicyRevision).not.toHaveBeenCalled();
      expect(activatePolicy).not.toHaveBeenCalled();

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

  it('has no acknowledgement control in any read state (AC-F18-a)', async () => {
    readPolicyActivation.mockResolvedValue(null);
    const fixture = await createPage();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="enable-collection-acknowledge"]')).toBeNull();
    expect(root.querySelector('input[type="checkbox"]')).toBeNull();
    expect(root.textContent).not.toContain('I understand this replaces the active policy');
    // The warning itself stays, and the control is described by it (AC-F18-c).
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

  it('clears the appended revision and the intent when the scope moves (F3)', async () => {
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

  it('re-reads the activation when the scope moves and shows what the new read answers (AC-T5.2-3)', async () => {
    // The first read (the host's initial project) already answered 404 during setup.
    readPolicyActivation.mockReset().mockResolvedValue({ ...ACTIVATION_STATE, project_id: 'project-2' });
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
});
