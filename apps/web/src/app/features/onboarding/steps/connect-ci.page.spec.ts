// Both server-confirmed paths, refusal/boundaries, and reveal lifetime.
import { computed, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type {
  CiBindingCreatedResponse,
  CiBindingView,
  IngestTokenCreatedResponse,
  IngestTokenView,
} from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { CONNECT_LIST_LIMIT } from '../data/onboarding-progress.service';
import { OnboardingStore } from '../onboarding.store';
import { ConnectCiPage } from './connect-ci.page';

const BINDING: CiBindingView = {
  id: 'binding-1',
  project_id: 'project-1',
  provider: 'github_actions',
  repository_id: 123,
  repository_full_name: 'acme/web',
  allowed_ref_patterns: ['refs/heads/main'],
  allowed_workflow_refs: ['acme/web/.github/workflows/ci.yml@refs/heads/main'],
  status: 'pending_verification',
  created_at: '2026-10-25T00:00:00Z',
  version: 1,
};
const TOKEN: IngestTokenView = {
  id: 'token-1',
  project_id: 'project-1',
  name: 'Build upload',
  prefix: 'egt_prefix',
  status: 'active',
  scopes: ['event:write', 'artifact:write', 'run:create'],
  expires_at: '2026-11-25T00:00:00Z',
  last_used_at: '2026-10-25T01:00:00Z',
  created_at: '2026-10-25T00:00:00Z',
};
const CHALLENGE = 'challenge-shown-once';
const SECRET = 'egt_one_time_secret_never_metadata';
const ROTATED = 'egt_successor_secret';

function failure(
  kind: ApiError['kind'],
  status: number,
  overrides: Partial<ApiError> = {},
): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: 'server_refusal',
    message: 'Exact server refusal, unchanged.',
    requestId: 'request-1',
    retryable: false,
    details: [{ path: '/repository_id', reason: 'Exact field reason.' }],
    retryAfterSeconds: null,
    ...overrides,
  });
}

describe('ConnectCiPage', () => {
  let fixture: ComponentFixture<ConnectCiPage>;
  let root: HTMLElement;
  let slot: RevealSlot;
  const permitted = signal(true);
  const bindings = signal<readonly CiBindingView[]>([]);
  const tokens = signal<readonly IngestTokenView[]>([]);
  const bindingsTruncated = signal(false);
  const tokensTruncated = signal(false);
  const project = signal<{ readonly slug: string } | null>({ slug: 'acme-web' });
  const refreshFailure = signal<'unavailable' | null>(null);
  const done = computed(() => ({ 2: bindings().length + tokens().length > 0 }));
  const canGoNext = computed(() => done()[2]);
  const createCiBinding = vi.fn<OnboardingDataService['createCiBinding']>();
  const createIngestToken = vi.fn<OnboardingDataService['createIngestToken']>();
  const rotateIngestToken = vi.fn<OnboardingDataService['rotateIngestToken']>();
  const refresh = vi.fn<(orgId: string, projectId: string) => Promise<void>>();

  beforeEach(async () => {
    permitted.set(true);
    bindings.set([]);
    tokens.set([]);
    bindingsTruncated.set(false);
    tokensTruncated.set(false);
    project.set({ slug: 'acme-web' });
    refreshFailure.set(null);
    createCiBinding
      .mockReset()
      .mockResolvedValue({ binding: BINDING, binding_challenge: CHALLENGE });
    createIngestToken.mockReset().mockResolvedValue({ token: TOKEN, secret: SECRET });
    rotateIngestToken.mockReset().mockResolvedValue({
      token: { ...TOKEN, id: 'token-2' },
      secret: ROTATED,
      overlap_ends_at: '2026-10-27T07:30:00Z',
    });
    refresh.mockReset().mockImplementation(() => {
      // As in the real store, only the server read, not the create response, changes done[2].
      if (createCiBinding.mock.calls.length > 0) bindings.set([BINDING]);
      if (createIngestToken.mock.calls.length > 0) tokens.set([TOKEN]);
      refreshFailure.set(null);
      return Promise.resolve();
    });
    await TestBed.configureTestingModule({
      imports: [ConnectCiPage],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: OnboardingDataService,
          useValue: { createCiBinding, createIngestToken, rotateIngestToken },
        },
        {
          provide: OnboardingStore,
          useValue: {
            bindings,
            tokens,
            bindingsTruncated,
            tokensTruncated,
            project,
            done,
            canGoNext,
            refreshFailure,
            refresh,
          },
        },
        {
          provide: SessionStore,
          useValue: {
            has: () => permitted(),
            // The page registers its health timer with the session boundary, so a fake store
            // without the registration seam would throw in the constructor.
            onSessionEnd: () => () => undefined,
          },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(ConnectCiPage);
    fixture.componentRef.setInput('orgId', 'org-1');
    fixture.componentRef.setInput('projectId', 'project-1');
    root = fixture.nativeElement as HTMLElement;
    slot = fixture.debugElement.injector.get(RevealSlot);
    await fixture.whenStable();
  });

  async function fill(values: Record<string, string>): Promise<void> {
    for (const [id, value] of Object.entries(values)) {
      const field = root.querySelector<HTMLInputElement | HTMLTextAreaElement>(
        `[data-testid="${id}"]`,
      )!;
      field.value = value;
      field.dispatchEvent(new Event('input', { bubbles: true }));
    }
    await fixture.whenStable();
  }

  async function fillOidc(overrides: Record<string, string> = {}): Promise<void> {
    await fill({
      'connect-oidc-repo-id': '123',
      'connect-oidc-repo-name': ' acme/web ',
      'connect-oidc-refs': ' refs/heads/main \n\n refs/tags/v* \n',
      'connect-oidc-workflows': ' acme/web/.github/workflows/ci.yml@refs/heads/main \n\n',
      ...overrides,
    });
  }

  async function submit(panel: 'oidc' | 'token'): Promise<void> {
    root
      .querySelector(`[data-testid="connect-${panel}-panel"] form`)!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
  }

  it('creates OIDC once with the exact numeric/trimmed SDK body, reveals, then refreshes', async () => {
    expect(canGoNext()).toBe(false);
    refresh.mockImplementationOnce(() => {
      expect(slot.current()).toEqual({ source: 'binding-challenge', value: CHALLENGE });
      expect(canGoNext()).toBe(false);
      bindings.set([BINDING]);
      return Promise.resolve();
    });
    await fillOidc();
    await submit('oidc');
    expect(createCiBinding).toHaveBeenCalledTimes(1);
    expect(createCiBinding).toHaveBeenCalledWith('project-1', {
      provider: 'github_actions',
      repository_id: 123,
      repository_full_name: 'acme/web',
      allowed_ref_patterns: ['refs/heads/main', 'refs/tags/v*'],
      allowed_workflow_refs: ['acme/web/.github/workflows/ci.yml@refs/heads/main'],
    });
    expect(refresh).toHaveBeenCalledWith('org-1', 'project-1');
    expect(canGoNext()).toBe(true);
    expect(
      root.querySelector('[data-testid="connect-binding-row"]')?.getAttribute('data-status'),
    ).toBe('pending_verification');
    expect(root.textContent).not.toMatch(/\bPASS\b|\bFAIL\b|Binding verified/);
  });

  it('creates an ingest token with name only and enables Next through the same refresh', async () => {
    expect(canGoNext()).toBe(false);
    refresh.mockImplementationOnce(() => {
      expect(slot.current()).toEqual({ source: 'ingest-token', value: SECRET });
      expect(canGoNext()).toBe(false);
      tokens.set([TOKEN]);
      return Promise.resolve();
    });
    await fill({ 'connect-token-name': ' Build upload ' });
    await submit('token');
    expect(createIngestToken).toHaveBeenCalledExactlyOnceWith('project-1', {
      name: 'Build upload',
    });
    expect(refresh).toHaveBeenCalledWith('org-1', 'project-1');
    expect(canGoNext()).toBe(true);
    expect(
      root.querySelector<HTMLTextAreaElement | HTMLInputElement>('[data-testid="reveal-value"]')
        ?.value,
    ).toBe(SECRET);
  });

  it.each(['', '0', '-1', '1.5', 'abc', '9007199254740992'])(
    'rejects invalid repository ID %j inline',
    async (id) => {
      await fillOidc({ 'connect-oidc-repo-id': id });
      await submit('oidc');
      expect(createCiBinding).not.toHaveBeenCalled();
      expect(root.querySelector('#connect-repo-id-error')?.textContent?.trim()).not.toBe('');
      expect(root.querySelector('#connect-oidc-repo-id')?.getAttribute('aria-invalid')).toBe(
        'true',
      );
    },
  );

  it('accepts the maximum safe integer repository ID', async () => {
    await fillOidc({ 'connect-oidc-repo-id': '9007199254740991' });
    await submit('oidc');
    expect(createCiBinding.mock.calls[0]?.[1].repository_id).toBe(9007199254740991);
    expect(root.querySelector('#connect-oidc-repo-id')?.getAttribute('aria-invalid')).toBeNull();
  });

  it('marks a refused field invalid and names its rendered reason, which is not a live region', async () => {
    createCiBinding.mockRejectedValueOnce(
      failure('validation', 422, {
        details: [
          { path: '/repository_full_name', reason: 'That repository is not visible to the app.' },
        ],
      }),
    );
    await fillOidc();
    await submit('oidc');
    const input = root.querySelector<HTMLInputElement>('#connect-oidc-repo-name')!;
    const reason = root.querySelector('#connect-repo-name-error')!;
    expect(reason.textContent).toContain('That repository is not visible to the app.');
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toBe(
      'connect-repo-name-hint connect-repo-name-error',
    );
    // The details alert says the same sentence; a live region here would say it twice.
    expect(reason.hasAttribute('aria-live')).toBe(false);
    for (const field of root.querySelectorAll<HTMLElement>('[aria-describedby]')) {
      for (const id of (field.getAttribute('aria-describedby') ?? '').split(/\s+/)) {
        if (id) expect(root.querySelector(`#${id}`), `${id} must be rendered`).not.toBeNull();
      }
    }
  });

  it('holds focus on the waiting line during the command, then on the refusal heading', async () => {
    let rejectPending: (error: unknown) => void = () => undefined;
    createCiBinding.mockImplementationOnce(
      () =>
        new Promise((_resolve, reject) => {
          rejectPending = reject;
        }),
    );
    await fillOidc();
    await submit('oidc');
    expect(document.activeElement).toBe(root.querySelector('[data-testid="connect-ci-busy"]'));
    rejectPending(failure('validation', 422));
    // The refusal renders after the command's own macrotask turn; the heading's effect runs then.
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    await fixture.whenStable();
    expect(document.activeElement).toBe(root.querySelector('#connect-failure-heading'));
  });

  it('strips control and format characters from server paths and reasons before display', async () => {
    createIngestToken.mockRejectedValueOnce(
      failure('validation', 422, {
        details: [
          {
            path: '/name\u0007\u202e',
            reason: 'Name \u0000must\u200f not embed\u2028 separators.',
          },
        ],
      }),
    );
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('token');
    const details = root.querySelector('[data-testid="connect-ci-details"]')!;
    expect(details.textContent).toContain('Token name:');
    expect(details.textContent).not.toContain('/name');
    expect(details.textContent).toContain('Name must not embed separators.');
    // The row still names its field, so the reason also appears beside that control.
    expect(root.querySelector('#connect-token-name-error')?.textContent).toContain(
      'Name must not embed separators.',
    );
    expect(root.querySelector('#connect-token-name')?.getAttribute('aria-invalid')).toBe('true');
    expect(root.textContent).not.toMatch(/[\u0000\u0007\u200f\u202e\u2028\u2029]/);
  });

  it('leaves no refusal cue behind once the refusal that armed it clears', async () => {
    createIngestToken.mockRejectedValueOnce(failure('validation', 422));
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('token');
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    await fixture.whenStable();
    expect(document.activeElement).toBe(root.querySelector('#connect-failure-heading'));
    // Starting the next command clears the refusal, and with it the heading the cue pointed at.
    await submit('token');
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    await fixture.whenStable();
    expect(root.querySelector('#connect-failure-heading')).toBeNull();
    const revealValue = root.querySelector('[data-testid="reveal-value"]');
    expect(document.activeElement).toBe(
      root.querySelector(`#${revealValue?.getAttribute('aria-labelledby')}`),
    );
  });

  it('consumes a refusal cue whose heading never renders', async () => {
    refresh.mockImplementationOnce(() => {
      // The reconcile answers under a different scope, so the refusal never reaches the screen.
      fixture.componentRef.setInput('projectId', 'project-2');
      return Promise.resolve();
    });
    createIngestToken.mockRejectedValueOnce(failure('timeout', 0));
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('token');
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="connect-ci-error"]')).toBeNull();
    // Nothing was rendered to move to: the visitor stays on the waiting line the press left.
    expect(document.activeElement).toBe(root.querySelector('[data-testid="connect-ci-busy"]'));
    // The spent cue cannot fire on a later render; the next refusal arms and moves focus once.
    createIngestToken.mockRejectedValueOnce(failure('validation', 422));
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('token');
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    await fixture.whenStable();
    expect(document.activeElement).toBe(root.querySelector('#connect-failure-heading'));
  });

  it('returns focus to the first panel heading when a re-read removes the retry notice', async () => {
    refreshFailure.set('unavailable');
    await fixture.whenStable();
    const retry = root.querySelector<HTMLButtonElement>(
      '[data-testid="connect-ci-refresh-failed"] button',
    )!;
    // Not natively disabled: the press keeps the focus it was made with while the re-read runs.
    expect(retry.disabled).toBe(false);
    retry.focus();
    retry.click();
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="connect-ci-refresh-failed"]')).toBeNull();
    // The notice held the only stop the visitor was on; the first panel below it takes it back.
    expect(document.activeElement).toBe(root.querySelector('#connect-oidc-heading'));
  });

  it.each(['connect-oidc-refs', 'connect-oidc-workflows'])(
    'rejects zero and over 32 lines in %s',
    async (id) => {
      for (const value of [' \n ', Array.from({ length: 33 }, (_, i) => `ref-${i}`).join('\n')]) {
        await fillOidc({ [id]: value });
        await submit('oidc');
        expect(createCiBinding).not.toHaveBeenCalled();
        const field = root.querySelector(`[data-testid="${id}"]`)!;
        expect(field.getAttribute('aria-invalid')).toBe('true');
        const errorId = field.getAttribute('aria-describedby')!.split(' ').at(-1);
        expect(root.querySelector(`#${errorId}`)?.textContent?.trim()).not.toBe('');
      }
    },
  );

  it('accepts the 32-line boundary and rejects an invalid owner/repo', async () => {
    await fillOidc({ 'connect-oidc-repo-name': 'invalid' });
    await submit('oidc');
    expect(createCiBinding).not.toHaveBeenCalled();
    expect(root.querySelector('#connect-oidc-repo-name')?.getAttribute('aria-invalid')).toBe(
      'true',
    );
    await fillOidc({
      'connect-oidc-refs': Array.from({ length: 32 }, (_, i) => `refs/heads/branch-${i}`).join(
        '\n',
      ),
    });
    await submit('oidc');
    expect(createCiBinding.mock.calls[0]?.[1].allowed_ref_patterns).toHaveLength(32);
  });

  it.each(['', '   ', 'n'.repeat(129), '\u{10400}'.repeat(129)])(
    'blocks invalid token name (%j)',
    async (name) => {
      await fill({ 'connect-token-name': name });
      await submit('token');
      expect(createIngestToken).not.toHaveBeenCalled();
      expect(root.querySelector('#connect-token-name')?.getAttribute('aria-invalid')).toBe('true');
      expect(root.querySelector('#connect-token-name-error')?.textContent?.trim()).not.toBe('');
    },
  );

  it.each(['n'.repeat(128), '\u{10400}'.repeat(128), '\u{10400}'.repeat(65)])(
    'accepts a token name of %s using Unicode code-point length',
    async (name) => {
      await fill({ 'connect-token-name': name });
      await submit('token');
      expect(createIngestToken).toHaveBeenCalledWith('project-1', { name });
      expect(root.querySelector('#connect-token-name')?.hasAttribute('maxlength')).toBe(false);
      expect(root.querySelector('#connect-token-name')?.getAttribute('aria-invalid')).toBeNull();
    },
  );

  it('allows only one in-flight command across both panels and rotation', async () => {
    let resolvePending!: (response: CiBindingCreatedResponse) => void;
    createCiBinding.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolvePending = resolve;
        }),
    );
    tokens.set([TOKEN]);
    await fillOidc();
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('oidc');
    await submit('oidc');
    await submit('token');
    for (const id of ['connect-oidc-submit', 'connect-token-submit', 'connect-token-rotate']) {
      expect(root.querySelector<HTMLButtonElement>(`[data-testid="${id}"]`)?.disabled).toBe(true);
    }
    expect(createCiBinding).toHaveBeenCalledTimes(1);
    expect(createIngestToken).not.toHaveBeenCalled();
    expect(rotateIngestToken).not.toHaveBeenCalled();
    resolvePending({ binding: BINDING, binding_challenge: CHALLENGE });
    await fixture.whenStable();
  });

  it.each([
    ['conflict', 409],
    ['forbidden', 403],
    ['not_found', 404],
    ['validation', 422],
    ['rate_limited', 429],
  ] as const)(
    'renders %s verbatim, re-enables controls and never auto-retries',
    async (kind, status) => {
      createIngestToken.mockRejectedValueOnce(failure(kind, status));
      await fill({ 'connect-token-name': 'Build upload' });
      await submit('token');
      const error = root.querySelector('[data-testid="connect-ci-error"]');
      expect(error?.textContent).toContain('Exact server refusal, unchanged.');
      expect(error?.textContent?.split('Exact server refusal, unchanged.')).toHaveLength(2);
      // Two alerts, each with its own sentence: the shared pattern announces the refusal title,
      // the page's labelled block announces the field rows (which the pattern never renders).
      expect(error?.querySelectorAll('[role="alert"]')).toHaveLength(2);
      expect(error?.querySelector('[role="alert"] [role="alert"]')).toBeNull();
      // The rows the server refused are spoken from one labelled alert, not bare paragraphs.
      const details = error?.querySelector<HTMLElement>('[data-testid="connect-ci-details"]');
      expect(details?.getAttribute('role')).toBe('alert');
      const detailsLabel = details?.getAttribute('aria-labelledby') ?? '';
      expect(root.querySelector(`#${detailsLabel}`)?.textContent).toContain('The server refused');
      expect(error?.querySelector('button')).toBeNull();
      expect(error?.querySelector('[data-testid="error-request-id"]')?.textContent).toContain(
        'request-1',
      );
      // The step shell owns the h2; the page's refusal heading sits below it and the pattern's
      // title one level below that, outside every alert so it can take focus.
      const pageHeading = error?.querySelector('h3.connect__failure-heading');
      expect(pageHeading).not.toBeNull();
      // The heading renders the class field's copy, never the template ref it is bound to.
      expect(pageHeading?.textContent?.trim()).toBe('The connection was not saved');
      expect(pageHeading?.closest('[role="alert"]')).toBeNull();
      expect(error?.querySelector('h4.state-panel__title')).not.toBeNull();
      expect(error?.querySelector('h2')).toBeNull();
      expect(error?.textContent).toContain('GitHub numeric repository ID: Exact field reason.');
      expect(error?.textContent).not.toContain('/repository_id');
      expect(error?.textContent).toContain('Exact field reason.');
      expect(
        root.querySelector<HTMLButtonElement>('[data-testid="connect-token-submit"]')?.disabled,
      ).toBe(false);
      expect(createIngestToken).toHaveBeenCalledTimes(1);
      expect(refresh).not.toHaveBeenCalled();
    },
  );

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

  it('derives connection health from the loaded lists and mounts the CLI quickstart', async () => {
    bindings.set([BINDING]);
    // A token without an expiry stays usable whatever the wall clock says on the test host.
    tokens.set([{ ...TOKEN, expires_at: null }]);
    await fixture.whenStable();
    const health = root.querySelector('[data-testid="connection-health"]');
    expect(health).not.toBeNull();
    expect(health?.getAttribute('data-health-state')).toBe('healthy');
    expect(root.querySelectorAll('[data-testid="connection-health-credential"]')).toHaveLength(2);
    const quickstart = root.querySelector('[data-testid="cli-quickstart"]');
    expect(quickstart).not.toBeNull();
    const snippet = quickstart?.querySelector('[data-testid="cli-quickstart-snippet"]');
    // Metadata only: the token by name and prefix, and the loaded project slug.
    expect(snippet?.textContent).toContain('Build upload');
    expect(snippet?.textContent).toContain('egt_prefix');
    expect(snippet?.textContent).toContain('acme-web');
    expect(createCiBinding).not.toHaveBeenCalled();
    expect(createIngestToken).not.toHaveBeenCalled();
    expect(rotateIngestToken).not.toHaveBeenCalled();
  });

  it('prefers an active usable token over an earlier rotating token, then falls back to rotating', async () => {
    const rotating: IngestTokenView = {
      ...TOKEN,
      id: 'rotating-token',
      name: 'Previous uploader',
      prefix: 'previous-prefix',
      status: 'rotated',
      expires_at: null,
      overlap_ends_at: '2099-01-01T00:00:00Z',
    };
    tokens.set([rotating, { ...TOKEN, expires_at: null }]);
    await fixture.whenStable();
    const snippet = root.querySelector('[data-testid="cli-quickstart-snippet"]')!;
    expect(snippet.textContent).toContain(TOKEN.name);
    expect(snippet.textContent).not.toContain(rotating.name);
    tokens.set([rotating, { ...TOKEN, status: 'revoked' }]);
    await fixture.whenStable();
    expect(snippet.textContent).toContain(rotating.name);
    expect(snippet.textContent).not.toContain(TOKEN.name);
  });

  it.each(['active', 'rotated'] as const)(
    'updates health and quickstart when the %s token boundary passes without a list change',
    async (status) => {
      vi.useFakeTimers({ toFake: ['Date', 'setTimeout', 'clearTimeout'] });
      vi.setSystemTime(new Date('2026-10-25T12:00:00Z'));
      try {
        const boundary = '2026-10-25T12:00:01Z';
        const listed: readonly IngestTokenView[] = [
          {
            ...TOKEN,
            status,
            expires_at: status === 'active' ? boundary : null,
            overlap_ends_at: status === 'rotated' ? boundary : null,
          },
        ];
        tokens.set(listed);
        fixture.detectChanges();
        const panel = root.querySelector('[data-testid="connection-health"]')!;
        const snippet = root.querySelector('[data-testid="cli-quickstart-snippet"]')!;
        expect(panel.getAttribute('data-health-state')).toBe('healthy');
        expect(snippet.textContent).toContain(TOKEN.name);
        await vi.advanceTimersByTimeAsync(999);
        fixture.detectChanges();
        expect(panel.getAttribute('data-health-state')).toBe('healthy');
        await vi.advanceTimersByTimeAsync(1);
        fixture.detectChanges();
        expect(panel.getAttribute('data-health-state')).toBe('unhealthy');
        expect(snippet.textContent).toContain('Create an ingest token first');
        expect(tokens()).toBe(listed);
        expect(canGoNext()).toBe(true);
        expect(refresh).not.toHaveBeenCalled();
      } finally {
        fixture.destroy();
        vi.useRealTimers();
      }
    },
  );

  it('clears its scheduled credential-boundary timer on destroy', () => {
    vi.useFakeTimers({ toFake: ['Date', 'setTimeout', 'clearTimeout'] });
    vi.setSystemTime(new Date('2026-10-25T12:00:00Z'));
    try {
      tokens.set([{ ...TOKEN, expires_at: '2026-10-25T12:00:01Z' }]);
      fixture.detectChanges();
      expect(vi.getTimerCount()).toBe(1);
      fixture.destroy();
      expect(vi.getTimerCount()).toBe(0);
    } finally {
      fixture.destroy();
      vi.useRealTimers();
    }
  });

  it('re-arms one timer for changed token metadata, including a far boundary', async () => {
    vi.useFakeTimers({ toFake: ['Date', 'setTimeout', 'clearTimeout'] });
    vi.setSystemTime(new Date('2026-10-25T12:00:00Z'));
    try {
      tokens.set([{ ...TOKEN, expires_at: '2026-10-25T12:00:01Z' }]);
      fixture.detectChanges();
      expect(vi.getTimerCount()).toBe(1);
      tokens.set([{ ...TOKEN, expires_at: '2099-01-01T00:00:00Z' }]);
      fixture.detectChanges();
      expect(vi.getTimerCount()).toBe(1);
      await vi.advanceTimersByTimeAsync(1000);
      fixture.detectChanges();
      expect(
        root.querySelector('[data-testid="connection-health"]')?.getAttribute('data-health-state'),
      ).toBe('healthy');
      tokens.set([{ ...TOKEN, expires_at: '2026-10-25T12:00:02Z' }]);
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(1000);
      fixture.detectChanges();
      expect(
        root.querySelector('[data-testid="connection-health"]')?.getAttribute('data-health-state'),
      ).toBe('unhealthy');
    } finally {
      fixture.destroy();
      vi.useRealTimers();
    }
  });

  it('references an OIDC binding by repository name when no token is usable', async () => {
    bindings.set([BINDING]);
    await fixture.whenStable();
    expect(
      root.querySelector('[data-testid="connection-health"]')?.getAttribute('data-health-state'),
    ).toBe('pending');
    const snippet = root.querySelector('[data-testid="cli-quickstart-snippet"]');
    expect(snippet?.textContent).toContain('acme/web');
    expect(snippet?.textContent).toContain('EVIDGATE_BINDING_CHALLENGE');
    // The challenge value itself is never in this snippet, only the variable name.
    expect(snippet?.textContent).not.toContain(CHALLENGE);
  });

  it('shows an unhealthy connection for a revoked-only project while step 2 stays done', async () => {
    tokens.set([{ ...TOKEN, status: 'revoked' }]);
    await fixture.whenStable();
    const health = root.querySelector('[data-testid="connection-health"]');
    expect(health?.getAttribute('data-health-state')).toBe('unhealthy');
    // Presence still completes step 2: the health panel reports, it does not gate.
    expect(canGoNext()).toBe(true);
    expect(root.querySelector('[data-testid="connect-token-row"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="connect-token-rotate"]')).toBeNull();
    expect(root.querySelector('[data-testid="cli-quickstart"]')?.textContent).toContain(
      'Create an ingest token first',
    );
  });

  it('names the token in the quickstart and keeps the one-time secret inside the reveal', async () => {
    refresh.mockImplementationOnce(() => {
      tokens.set([{ ...TOKEN, expires_at: null }]);
      return Promise.resolve();
    });
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('token');
    expect(slot.current()).toEqual({ source: 'ingest-token', value: SECRET });
    expect(root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]')?.value).toBe(
      SECRET,
    );
    const quickstart = root.querySelector('[data-testid="cli-quickstart"]')!;
    const snippet = quickstart.querySelector('[data-testid="cli-quickstart-snippet"]');
    expect(snippet?.textContent).toContain('Build upload');
    expect(snippet?.textContent).toContain('egt_prefix');
    expect(snippet?.textContent).not.toContain(SECRET);
    expect(quickstart.textContent).not.toContain(SECRET);
    expect(root.querySelector('[data-testid="connection-health"]')?.textContent).not.toContain(
      SECRET,
    );
    // The only place the value exists is the component-scoped reveal input.
    expect(root.outerHTML).not.toContain(SECRET);
    expect(root.querySelector('[data-testid="connect-reveal"]')).not.toBeNull();
  });
});
