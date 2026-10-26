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
import { provideZonelessChangeDetection, signal } from '@angular/core';
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

  /** The form's submit event: the one path a real Enter key or button click takes. */
  function pressConfirm(): void {
    root
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
  }

  /**
   * Let the chain the page awaits run to its request. `whenStable` covers the framework's own work;
   * the macrotask turn before it drains the mocked promise the page awaits between the append and
   * the activation, so an assertion never reads a half-run chain.
   */
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();
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

  it('previews the default advisory policy and states that nothing is gated yet (AC5)', () => {
    const preview = text('[data-testid="enable-collection-policy-preview"]');
    // T62: the 24 h late-evidence window, in minutes, as the body carries it.
    expect(preview).toContain('1440');
    expect(preview).toContain('Not gating yet');
    expect(preview).toContain('advisory mode');
    // The preview is the constant body: nothing is required of a project that configured nothing.
    expect(preview).toContain('Required suites');
    expect(preview).toContain('None');
    expect(text('[data-testid="enable-collection-not-gating"]')).toContain('never blocks a merge');
    // The lone creator holds `policy:write` (T98), so the confirm is available unaided.
    expect(has).toHaveBeenCalledWith('policy:write');
    expect(confirmButton().disabled).toBe(false);
    expect(root.querySelector('[data-testid="enable-collection-ask-approver"]')).toBeNull();
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
    expect(confirmButton().getAttribute('aria-describedby')).toBe('enable-permission');
    expect(text('[data-testid="enable-collection-ask-approver"]')).toContain(
      'Ask your release approver',
    );
    pressConfirm();
    await settle();
    // Disabled is not decoration: neither call leaves the page.
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
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
});
