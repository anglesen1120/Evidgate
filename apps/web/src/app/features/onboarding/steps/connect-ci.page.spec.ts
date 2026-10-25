// 09.2.1-T2: both server-confirmed paths, refusal/boundaries, and reveal lifetime.
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

function failure(kind: ApiError['kind'], status: number): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: 'server_refusal',
    message: 'Exact server refusal, unchanged.',
    requestId: 'request-1',
    retryable: false,
    details: [{ path: '/repository_id', reason: 'Exact field reason.' }],
    retryAfterSeconds: null,
  });
}

describe('ConnectCiPage', () => {
  let fixture: ComponentFixture<ConnectCiPage>;
  let root: HTMLElement;
  let slot: RevealSlot;
  const permitted = signal(true);
  const bindings = signal<readonly CiBindingView[]>([]);
  const tokens = signal<readonly IngestTokenView[]>([]);
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
          useValue: { bindings, tokens, done, canGoNext, refreshFailure, refresh },
        },
        { provide: SessionStore, useValue: { has: () => permitted() } },
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
    expect(createCiBinding).toHaveBeenCalledWith('org-1', 'project-1', {
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
    expect(root.textContent).toContain('Pending verification');
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
      expect(root.querySelector('#connect-repo-id-error')?.textContent).toContain(
        'positive whole-number',
      );
      expect(root.querySelector('#connect-oidc-repo-id')?.getAttribute('aria-invalid')).toBe(
        'true',
      );
    },
  );

  it.each(['connect-oidc-refs', 'connect-oidc-workflows'])(
    'rejects zero and over 32 lines in %s',
    async (id) => {
      for (const value of [' \n ', Array.from({ length: 33 }, (_, i) => `ref-${i}`).join('\n')]) {
        await fillOidc({ [id]: value });
        await submit('oidc');
        expect(createCiBinding).not.toHaveBeenCalled();
        expect(root.textContent).toContain('between 1 and 32 non-empty');
      }
    },
  );

  it('accepts the 32-line boundary and rejects an invalid owner/repo', async () => {
    await fillOidc({ 'connect-oidc-repo-name': 'invalid' });
    await submit('oidc');
    expect(createCiBinding).not.toHaveBeenCalled();
    expect(root.textContent).toContain('Enter the repository as owner/repo.');
    await fillOidc({
      'connect-oidc-refs': Array.from({ length: 32 }, (_, i) => `refs/heads/branch-${i}`).join(
        '\n',
      ),
    });
    await submit('oidc');
    expect(createCiBinding.mock.calls[0]?.[2].allowed_ref_patterns).toHaveLength(32);
  });

  it.each(['', '   ', 'n'.repeat(129)])('blocks invalid token name (%j)', async (name) => {
    await fill({ 'connect-token-name': name });
    await submit('token');
    expect(createIngestToken).not.toHaveBeenCalled();
    expect(root.querySelector('#connect-token-name-error')?.textContent).toContain(
      'between 1 and 128',
    );
  });

  it('accepts a 128-character token name', async () => {
    await fill({ 'connect-token-name': 'n'.repeat(128) });
    await submit('token');
    expect(createIngestToken).toHaveBeenCalledWith('project-1', { name: 'n'.repeat(128) });
  });

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
      expect(error?.textContent).toContain('/repository_id');
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
      expect(
        root.querySelector(`#${button.getAttribute('aria-describedby')}`)?.textContent,
      ).toContain('Ask your organisation owner');
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
    expect(root.textContent).not.toContain('24 h');
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
});
