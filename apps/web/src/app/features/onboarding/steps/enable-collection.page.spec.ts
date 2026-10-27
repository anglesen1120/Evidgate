// 09.2.1-T5 (plan 09.2.1 §2 AC5, §8 rows T98 and 00 §9.1 idempotency, §9 test-plan rows for
// `enable-collection.page.spec.ts`, §10 T5.1/T5.2): the step-5 page, end to end over a mocked data
// service.
//
// What each case here is allowed to prove, and what it is not:
// - The page never derives "already enabled" locally. There is no read of an activation (queue row
//   F18), so every case starts from the confirm; the reload case is exactly the repeat confirm,
//   which the server answers with `changed: false` and still audits (07.1.1-T4).
// - "One audited `policy_activation` row" (§9 line 124) is the SERVER's half: the page can only
//   prove it sends one chain, one stable `Idempotency-Key` and the identical policy body, which is
//   what the contract's replay rule needs. The audit-cardinality assertion lives in
//   `tests/Tenancy.IntegrationTests/PolicyActivationTests.cs`, not in a component spec.
// - The disabled state is about a missing `policy:write` (a `release_approver` reassigned away,
//   AC5/T98), not about a server refusal: a refusal is rendered as the typed error state instead.
import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { PolicyActivationDto, PolicyRevisionDto } from '@evidgate/sdk';

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

describe('EnableCollectionPage', () => {
  let fixture: ComponentFixture<EnableCollectionPage>;
  let root: HTMLElement;
  const permitted = signal(true);
  /** The session's grant check: true only for the exact code the step names (AC5: `policy:write`). */
  const has = vi.fn((code: string) => permitted() && code === 'policy:write');
  const createPolicyRevision = vi.fn<OnboardingDataService['createPolicyRevision']>();
  const activatePolicy = vi.fn<OnboardingDataService['activatePolicy']>();

  beforeEach(async () => {
    permitted.set(true);
    has.mockClear();
    createPolicyRevision.mockReset().mockResolvedValue(REVISION);
    activatePolicy.mockReset().mockResolvedValue(ACTIVATION);
    await TestBed.configureTestingModule({
      imports: [EnableCollectionPage],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: OnboardingDataService,
          useValue: { createPolicyRevision, activatePolicy },
        },
        { provide: SessionStore, useValue: { has } },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(EnableCollectionPage);
    fixture.componentRef.setInput('orgId', 'org-1');
    fixture.componentRef.setInput('projectId', 'project-1');
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  function acknowledgeBox(): HTMLInputElement {
    return root.querySelector<HTMLInputElement>('[data-testid="enable-collection-acknowledge"]')!;
  }

  /** The acknowledgement the step requires before its confirm is pressable (D-S104-Q2). */
  function acknowledge(): void {
    const box = acknowledgeBox();
    if (!box.checked) {
      box.checked = true;
      box.dispatchEvent(new Event('change', { bubbles: true }));
    }
  }

  /** The path a real confirm takes: the acknowledgement first, then the form's submit. */
  function pressConfirm(): void {
    acknowledge();
    pressConfirmUnacknowledged();
  }

  /** The form's submit event without the acknowledgement, i.e. what a disabled control must not do. */
  function pressConfirmUnacknowledged(): void {
    root
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
  }

  /**
   * Let the chain the page awaits run to its request. `whenStable` covers the framework's own work;
   * the macrotask turn before it drains the mocked promise the page awaits between the append and
   * the activation, so an assertion never reads a half-run chain.
   */
  async function drain(target: ComponentFixture<unknown>): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await target.whenStable();
  }

  function settle(): Promise<void> {
    return drain(fixture);
  }

  function confirmButton(): HTMLButtonElement {
    return root.querySelector<HTMLButtonElement>('[data-testid="enable-collection-confirm"]')!;
  }

  function retryButton(): HTMLButtonElement | null {
    return root.querySelector<HTMLButtonElement>('app-error-pattern button');
  }

  function text(selector: string): string {
    return root.querySelector(selector)?.textContent?.trim() ?? '';
  }

  it('previews the default advisory policy and states that nothing is gated yet (AC5)', async () => {
    const preview = text('[data-testid="enable-collection-policy-preview"]');
    // T62: the 24 h late-evidence window, in minutes, as the body carries it.
    expect(preview).toContain('1440');
    expect(preview).toContain('Not gating yet');
    expect(preview).toContain('advisory mode');
    // The preview is the constant body: nothing is required of a project that configured nothing.
    expect(preview).toContain('Required suites');
    expect(preview).toContain('None');
    expect(text('[data-testid="enable-collection-not-gating"]')).toContain('never blocks a merge');
    // The lone creator holds `policy:write` (T98)…
    expect(has).toHaveBeenCalledWith('policy:write');
    expect(root.querySelector('[data-testid="enable-collection-ask-approver"]')).toBeNull();
    // …but D-S104-Q2 still requires the hazard acknowledgement before the press.
    expect(text('[data-testid="enable-collection-replaces-active"]')).toContain(
      'Gating stops until a gating policy is activated again',
    );
    expect(confirmButton().disabled).toBe(true);
    expect(acknowledgeBox().checked).toBe(false);
    expect(confirmButton().getAttribute('aria-describedby')).toBe('enable-replaces-warning');

    acknowledge();
    await settle();
    expect(confirmButton().disabled).toBe(false);
    expect(confirmButton().getAttribute('aria-describedby')).toBeNull();
  });

  it('requires the hazard acknowledgement before the confirm is pressable (D-S104-Q2)', async () => {
    const box = acknowledgeBox();
    // A native checkbox with a visible, associated label: the acknowledgement is not a hidden flag.
    expect(box.type).toBe('checkbox');
    expect(box.disabled).toBe(false);
    const label = root.querySelector<HTMLLabelElement>('label[for="enable-acknowledge"]');
    expect(label).not.toBeNull();
    expect(label!.textContent?.trim()).toBe('I understand this replaces the active policy.');

    // Unacknowledged, the form cannot be submitted: no append, no activation, no intent.
    pressConfirmUnacknowledged();
    await settle();
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();

    acknowledge();
    await settle();
    pressConfirm();
    await settle();
    // Acknowledged, the same submit runs the one chain.
    expect(createPolicyRevision).toHaveBeenCalledTimes(1);
    expect(activatePolicy).toHaveBeenCalledTimes(1);
  });

  it('withdraws the acknowledgement and its action when the grant goes away (T98)', async () => {
    acknowledge();
    await settle();

    permitted.set(false);
    await settle();

    // Unchecked and disabled: a withdrawn grant must not leave a stale acknowledgement behind.
    expect(acknowledgeBox().checked).toBe(false);
    expect(acknowledgeBox().disabled).toBe(true);
    expect(confirmButton().disabled).toBe(true);
    // Both reasons are named, so the disabled control explains itself to assistive tech.
    expect(confirmButton().getAttribute('aria-describedby')).toBe(
      'enable-permission enable-replaces-warning',
    );
  });

  it('appends the exact default body once, then activates it in advisory mode, and shows the terminal panel (AC5)', async () => {
    pressConfirm();
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
    expect(root.querySelector('[data-testid="enable-collection-confirm"]')).toBeNull();
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
    pressConfirm();
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

    expect(confirmButton().disabled).toBe(true);
    expect(confirmButton().getAttribute('aria-describedby')).toContain('enable-permission');
    expect(text('[data-testid="enable-collection-ask-approver"]')).toContain(
      'Ask your release approver',
    );
    // The acknowledgement is not a way around the grant: its own control is disabled and unchecked.
    expect(acknowledgeBox().disabled).toBe(true);
    expect(acknowledgeBox().checked).toBe(false);
    pressConfirmUnacknowledged();
    await settle();
    // Disabled is not decoration: neither call leaves the page.
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
  });

  it('keeps the retry action closed while the acknowledgement is withdrawn (D-S104-Q2)', async () => {
    activatePolicy.mockRejectedValueOnce(failure('timeout', 0, { retryable: true }));
    pressConfirm();
    await settle();
    expect(activatePolicy).toHaveBeenCalledTimes(1);
    expect(retryButton()!.disabled).toBe(false);

    // A retry is the same intent, so it must be as deliberate as the first press.
    acknowledgeBox().checked = false;
    acknowledgeBox().dispatchEvent(new Event('change', { bubbles: true }));
    await settle();

    expect(confirmButton().disabled).toBe(true);
    expect(retryButton()!.disabled).toBe(true);
    pressConfirmUnacknowledged();
    await settle();
    expect(activatePolicy).toHaveBeenCalledTimes(1);
  });

  it('sends one intent for two synchronous presses and one stable activation key', async () => {
    let release!: (activation: PolicyActivationDto) => void;
    activatePolicy.mockImplementationOnce(
      () =>
        new Promise<PolicyActivationDto>((resolve) => {
          release = resolve;
        }),
    );

    // Both submits run in the same task, before any await could settle the first chain.
    pressConfirm();
    pressConfirm();
    await settle();
    expect(createPolicyRevision).toHaveBeenCalledTimes(1);
    expect(activatePolicy).toHaveBeenCalledTimes(1);

    const key = activatePolicy.mock.calls[0]![2].idempotencyKey;
    release(ACTIVATION);
    await settle();
    expect(activatePolicy.mock.calls[0]![2].idempotencyKey).toBe(key);
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).not.toBeNull();
    // The double press opened exactly one chain, so a third press after success is irrelevant:
    // the key from the first press was the only one sent.
    expect(activatePolicy.mock.calls.map(([, , intent]) => intent.idempotencyKey)).toEqual([key]);
  });

  it('retries an uncertain activation with the same key and without appending again (00 §9.1)', async () => {
    activatePolicy.mockRejectedValueOnce(failure('timeout', 0, { retryable: true }));
    pressConfirm();
    await settle();

    // A transport failure claims no outcome: the panel says so and offers the same-intent retry.
    expect(root.querySelector('[data-testid="enable-collection-error"]')).not.toBeNull();
    expect(text('[data-testid="enable-collection-response-uncertain"]')).toContain(
      'may already be recorded',
    );
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
    const retry = retryButton();
    expect(retry).not.toBeNull();
    expect(retry!.disabled).toBe(false);

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
      pressConfirm();
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
      expect(retryButton()).toBeNull();
    },
  );

  it('never renders bidi or control characters a server label carries (T47 precedent)', async () => {
    // U+202E (right-to-left override) and U+009B (C1 control) inside the digest, i.e. inside the
    // characters the abbreviation shows — a sanitised-after-abbreviation bug would surface here.
    const hostileHash = 'sha256:9f2c\u202E\u009B1d7b4a6e8f0a';
    activatePolicy.mockResolvedValue({ ...ACTIVATION, hash: hostileHash });
    pressConfirm();
    await settle();

    const enabled = root.querySelector('[data-testid="enable-collection-enabled"]')!;
    expect(enabled.textContent).not.toContain('\u202E');
    expect(enabled.textContent).not.toContain('\u009B');
    expect(enabled.textContent).toContain('sha256:9f2c1d7b4a6e…');
    // The label is still interpolated text inside `<bdi>`, never markup.
    expect(enabled.querySelector('code')?.innerHTML).not.toContain('&lt;');
  });

  it('says confirming replaces the active policy, and names the revision it replaced (F1)', async () => {
    // The hazard is stated before the press: the confirm is a REPLACE, and gating stops with it.
    const warning = text('[data-testid="enable-collection-replaces-active"]');
    expect(warning).toContain('replaces whatever policy is active now');
    expect(warning).toContain('Gating stops until a gating policy is activated again');

    activatePolicy.mockResolvedValue({
      ...ACTIVATION,
      previous_policy_revision_id: '018f0000-0000-7000-8000-00000000a003',
    });
    pressConfirm();
    await settle();

    const enabled = root.querySelector('[data-testid="enable-collection-enabled"]')!;
    expect(enabled.textContent).toContain('Revision this replaced');
    expect(enabled.textContent).toContain('018f0000-0000-7000-8000-00000000a003');
  });

  it('omits the replaced-revision row when the answer names none', async () => {
    pressConfirm();
    await settle();

    const enabled = root.querySelector('[data-testid="enable-collection-enabled"]')!;
    expect(enabled.textContent).not.toContain('Revision this replaced');
  });

  it('renders the typed error instead of a terminal panel for a mode it cannot present (F2)', async () => {
    activatePolicy.mockResolvedValue({ ...ACTIVATION, mode: 'enforcing' });
    pressConfirm();
    await settle();

    // No success surface, and the advisory-only sentences are not asserted about this answer.
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
    expect(root.textContent).not.toContain('Project enabled and collecting');
    const error = root.querySelector('[data-testid="enable-collection-error"]');
    expect(error).not.toBeNull();
    expect(error!.textContent).toContain('cannot present the policy mode');
  });

  it('renders the shared "Unavailable" copy instead of throwing on a malformed answer (F5)', async () => {
    activatePolicy.mockResolvedValue({
      ...ACTIVATION,
      hash: '',
      activated_at: 'not-a-date',
    });
    pressConfirm();
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
    pressConfirm();
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
    pressConfirm();
    await settle();

    // The runner owns the window: while it is open the action could only be a no-op, so the control
    // says so natively instead of inviting a press. (The window opening is the runner's own spec.)
    expect(confirmButton().disabled).toBe(true);
    expect(retryButton()?.disabled).toBe(true);
  });

  it('moves focus to the terminal heading when the confirm goes away (QA-01)', async () => {
    pressConfirm();
    await settle();

    const heading = root.querySelector('#enable-enabled-heading');
    expect(heading).not.toBeNull();
    expect(heading!.getAttribute('tabindex')).toBe('-1');
    expect(document.activeElement).toBe(heading);
  });

  it('re-appends after a failed append and keeps one activation key across attempts (R4)', async () => {
    createPolicyRevision.mockRejectedValueOnce(failure('timeout', 0, { retryable: true }));
    pressConfirm();
    await settle();
    expect(createPolicyRevision).toHaveBeenCalledTimes(1);
    expect(activatePolicy).not.toHaveBeenCalled();

    activatePolicy.mockRejectedValueOnce(failure('timeout', 0, { retryable: true }));
    retryButton()!.click();
    await settle();
    // The append is retried because this intent produced no revision yet.
    expect(createPolicyRevision).toHaveBeenCalledTimes(2);
    expect(activatePolicy).toHaveBeenCalledTimes(1);

    retryButton()!.click();
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
    pressConfirm();
    await settle();
    expect(retryButton()).toBeNull();

    pressConfirm();
    await settle();

    expect(activatePolicy).toHaveBeenCalledTimes(2);
    // A new intent means a new key — and therefore a fresh append, whose body is still identical.
    expect(createPolicyRevision).toHaveBeenCalledTimes(2);
    const keys = activatePolicy.mock.calls.map(([, , intent]) => intent.idempotencyKey);
    expect(keys[0]).not.toBe(keys[1]);
  });

  it('retries a retryable failure through the main confirm button with the same key (R4)', async () => {
    activatePolicy.mockRejectedValueOnce(failure('timeout', 0, { retryable: true }));
    pressConfirm();
    await settle();

    // The main control, not the error panel's action: `run()` from a retryable failure keeps the key.
    pressConfirm();
    await settle();

    expect(createPolicyRevision).toHaveBeenCalledTimes(1);
    expect(activatePolicy).toHaveBeenCalledTimes(2);
    const keys = activatePolicy.mock.calls.map(([, , intent]) => intent.idempotencyKey);
    expect(keys[0]).toBe(keys[1]);
    expect(root.querySelector('[data-testid="enable-collection-enabled"]')).not.toBeNull();
  });

  it('sends no activation when the append settles after the step was left (F3)', async () => {
    let release!: (revision: PolicyRevisionDto) => void;
    createPolicyRevision.mockImplementationOnce(
      () =>
        new Promise<PolicyRevisionDto>((resolve) => {
          release = resolve;
        }),
    );
    pressConfirm();
    await settle();
    expect(createPolicyRevision).toHaveBeenCalledTimes(1);

    fixture.destroy();
    release(REVISION);
    await new Promise((resolve) => setTimeout(resolve, 0));

    // The half-written append is one immutable revision (content-idempotent); the command that would
    // change the project's policy pointer must never leave for a view the user has left.
    expect(activatePolicy).not.toHaveBeenCalled();
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
  let host: ComponentFixture<EnableCollectionHost>;
  let hostRoot: HTMLElement;

  function acknowledgeBox(): HTMLInputElement {
    return hostRoot.querySelector<HTMLInputElement>(
      '[data-testid="enable-collection-acknowledge"]',
    )!;
  }

  /** The path a real confirm takes (the acknowledgement first, then the submit). */
  function press(): void {
    const box = acknowledgeBox();
    if (!box.checked) {
      box.checked = true;
      box.dispatchEvent(new Event('change', { bubbles: true }));
    }
    hostRoot
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
  }

  async function drain(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await host.whenStable();
  }

  beforeEach(async () => {
    permitted.set(true);
    createPolicyRevision.mockReset().mockResolvedValue(REVISION);
    activatePolicy.mockReset().mockResolvedValue(ACTIVATION);
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [EnableCollectionHost],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: OnboardingDataService,
          useValue: { createPolicyRevision, activatePolicy },
        },
        { provide: SessionStore, useValue: { has } },
      ],
    }).compileComponents();
    host = TestBed.createComponent(EnableCollectionHost);
    hostRoot = host.nativeElement as HTMLElement;
    await host.whenStable();
  });

  it('clears the appended revision and the intent when the scope moves (F3)', async () => {
    press();
    await drain();
    expect(hostRoot.querySelector('[data-testid="enable-collection-enabled"]')).not.toBeNull();
    const firstKey = activatePolicy.mock.calls[0]![2].idempotencyKey;

    // The wizard moved to another project: the terminal panel describes an answer for the old one.
    host.componentInstance.projectId.set('project-2');
    await drain();
    expect(hostRoot.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
    expect(hostRoot.querySelector('[data-testid="enable-collection-confirm"]')).not.toBeNull();
    // …and the new scope starts unacknowledged: consent for project-1 is not consent for project-2.
    expect(acknowledgeBox().checked).toBe(false);
    expect(
      hostRoot.querySelector<HTMLButtonElement>('[data-testid="enable-collection-confirm"]')!
        .disabled,
    ).toBe(true);

    press();
    await drain();
    // A new intent for the new scope: new key, and the cached revision of the old intent is gone.
    expect(createPolicyRevision).toHaveBeenCalledTimes(2);
    expect(createPolicyRevision).toHaveBeenLastCalledWith('project-2', DEFAULT_ADVISORY_POLICY);
    expect(activatePolicy).toHaveBeenCalledTimes(2);
    expect(activatePolicy.mock.calls[1]![2].idempotencyKey).not.toBe(firstKey);
  });
});
