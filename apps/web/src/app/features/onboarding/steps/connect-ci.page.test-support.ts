import { computed, provideZonelessChangeDetection, signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import type { CiBindingView, IngestTokenView } from '@evidgate/sdk';
import { vi } from 'vitest';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { OnboardingStore } from '../onboarding.store';
import { provideDesktopOnboardingViewport } from '../onboarding-viewport.testing';
import { ConnectCiPage } from './connect-ci.page';

export const BINDING: CiBindingView = {
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
export const TOKEN: IngestTokenView = {
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
export const CHALLENGE = 'challenge-shown-once';
export const SECRET = 'egt_one_time_secret_never_metadata';
export const ROTATED = 'egt_successor_secret';

export function failure(
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

/** Shared setup keeps resource-derived completion and the page-scoped reveal slot identical. */
export class ConnectionPageTestContext {
  fixture!: ComponentFixture<ConnectCiPage>;
  root!: HTMLElement;
  slot!: RevealSlot;
  readonly permitted = signal(true);
  readonly bindings = signal<readonly CiBindingView[]>([]);
  readonly tokens = signal<readonly IngestTokenView[]>([]);
  readonly bindingsTruncated = signal(false);
  readonly tokensTruncated = signal(false);
  readonly project = signal<{ readonly slug: string } | null>({ slug: 'acme-web' });
  readonly refreshFailure = signal<'unavailable' | null>(null);
  readonly done = computed(() => ({ 2: this.bindings().length + this.tokens().length > 0 }));
  readonly canGoNext = computed(() => this.done()[2]);
  readonly createCiBinding = vi.fn<OnboardingDataService['createCiBinding']>();
  readonly createIngestToken = vi.fn<OnboardingDataService['createIngestToken']>();
  readonly rotateIngestToken = vi.fn<OnboardingDataService['rotateIngestToken']>();
  readonly refresh = vi.fn<(orgId: string, projectId: string) => Promise<void>>();
  private readonly store = {
    bindings: this.bindings,
    tokens: this.tokens,
    bindingsTruncated: this.bindingsTruncated,
    tokensTruncated: this.tokensTruncated,
    project: this.project,
    done: this.done,
    canGoNext: this.canGoNext,
    refreshFailure: this.refreshFailure,
    refresh: this.refresh,
  };

  async mount(): Promise<void> {
    this.permitted.set(true);
    this.bindings.set([]);
    this.tokens.set([]);
    this.bindingsTruncated.set(false);
    this.tokensTruncated.set(false);
    this.project.set({ slug: 'acme-web' });
    this.refreshFailure.set(null);
    this.createCiBinding
      .mockReset()
      .mockResolvedValue({ binding: BINDING, binding_challenge: CHALLENGE });
    this.createIngestToken.mockReset().mockResolvedValue({ token: TOKEN, secret: SECRET });
    this.rotateIngestToken.mockReset().mockResolvedValue({
      token: { ...TOKEN, id: 'token-2' },
      secret: ROTATED,
      overlap_ends_at: '2026-10-27T07:30:00Z',
    });
    this.refresh.mockReset().mockImplementation(() => {
      // Only metadata read from the server, not a create answer, changes done[2].
      if (this.createCiBinding.mock.calls.length > 0) this.bindings.set([BINDING]);
      if (this.createIngestToken.mock.calls.length > 0) this.tokens.set([TOKEN]);
      this.refreshFailure.set(null);
      return Promise.resolve();
    });
    await TestBed.configureTestingModule({
      imports: [ConnectCiPage],
      providers: [
        provideZonelessChangeDetection(),
        provideDesktopOnboardingViewport(),
        {
          provide: OnboardingDataService,
          useValue: {
            createCiBinding: this.createCiBinding,
            createIngestToken: this.createIngestToken,
            rotateIngestToken: this.rotateIngestToken,
          },
        },
        { provide: OnboardingStore, useValue: this.store },
        {
          provide: SessionStore,
          useValue: { has: () => this.permitted(), onSessionEnd: () => () => undefined },
        },
      ],
    }).compileComponents();
    this.fixture = TestBed.createComponent(ConnectCiPage);
    this.fixture.componentRef.setInput('orgId', 'org-1');
    this.fixture.componentRef.setInput('projectId', 'project-1');
    this.root = this.fixture.nativeElement as HTMLElement;
    this.slot = this.fixture.debugElement.injector.get(RevealSlot);
    await this.fixture.whenStable();
  }

  readonly fill = async (values: Record<string, string>): Promise<void> => {
    for (const [id, value] of Object.entries(values)) {
      const field = this.root.querySelector<HTMLInputElement | HTMLTextAreaElement>(
        `[data-testid="${id}"]`,
      )!;
      field.value = value;
      field.dispatchEvent(new Event('input', { bubbles: true }));
    }
    await this.fixture.whenStable();
  };

  readonly fillOidc = async (overrides: Record<string, string> = {}): Promise<void> => {
    await this.fill({
      'connect-oidc-repo-id': '123',
      'connect-oidc-repo-name': ' acme/web ',
      'connect-oidc-refs': ' refs/heads/main \n\n refs/tags/v* \n',
      'connect-oidc-workflows': ' acme/web/.github/workflows/ci.yml@refs/heads/main \n\n',
      ...overrides,
    });
  };

  readonly submit = async (panel: 'oidc' | 'token'): Promise<void> => {
    this.root
      .querySelector(`[data-testid="connect-${panel}-panel"] form`)!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await this.fixture.whenStable();
  };
}

export function createConnectionPageContext(): ConnectionPageTestContext {
  return new ConnectionPageTestContext();
}
