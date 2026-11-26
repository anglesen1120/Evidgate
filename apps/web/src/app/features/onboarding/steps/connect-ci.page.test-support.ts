import { provideZonelessChangeDetection, signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import type { IngestTokenView } from '@evidgate/sdk';
import { vi } from 'vitest';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { OnboardingStore, type OnboardingFailure } from '../onboarding.store';
import { OnboardingTokenStore } from '../onboarding-token.store';
import { provideDesktopOnboardingViewport } from '../onboarding-viewport.testing';
import { ConnectCiPage } from './connect-ci.page';

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
    details: [{ path: '/name', reason: 'Exact field reason.' }],
    retryAfterSeconds: null,
    ...overrides,
  });
}

type TokenInventory = InstanceType<typeof OnboardingTokenStore>;

export function provideTokenInventory(
  inventory: Pick<TokenInventory, 'tokens'> &
    Partial<
      Pick<
        TokenInventory,
        | 'tokensTruncated'
        | 'tokensRefreshing'
        | 'tokensLoaded'
        | 'tokensRefreshFailure'
        | 'setTokenScope'
        | 'clearTokenScope'
        | 'refreshTokens'
      >
    >,
): void {
  TestBed.overrideComponent(ConnectCiPage, {
    remove: { providers: [OnboardingTokenStore] },
    add: {
      providers: [
        {
          provide: OnboardingTokenStore,
          useValue: {
            tokensTruncated: signal(false),
            tokensRefreshing: signal(false),
            tokensLoaded: signal(true),
            tokensRefreshFailure: signal(null),
            setTokenScope: vi.fn(),
            clearTokenScope: vi.fn(),
            refreshTokens: vi.fn().mockResolvedValue(undefined),
            ...inventory,
          },
        },
      ],
    },
  });
}

export class ConnectionPageTestContext {
  fixture!: ComponentFixture<ConnectCiPage>;
  root!: HTMLElement;
  slot!: RevealSlot;
  readonly permitted = signal(true);
  readonly tokens = signal<readonly IngestTokenView[]>([]);
  readonly tokensLoaded = signal(true);
  readonly setTokenScope = vi.fn();
  readonly clearTokenScope = vi.fn();
  readonly tokensTruncated = signal(false);
  readonly project = signal<{ readonly slug: string } | null>({ slug: 'acme-web' });
  readonly refreshFailure = signal<OnboardingFailure>(null);
  readonly createIngestToken = vi.fn<OnboardingDataService['createIngestToken']>();
  readonly rotateIngestToken = vi.fn<OnboardingDataService['rotateIngestToken']>();
  readonly refresh = vi.fn<(orgId: string, projectId: string) => Promise<void>>();

  async mount(active = true): Promise<void> {
    this.permitted.set(true);
    this.tokens.set([]);
    this.tokensLoaded.set(true);
    this.setTokenScope.mockClear();
    this.clearTokenScope.mockClear();
    this.tokensTruncated.set(false);
    this.project.set({ slug: 'acme-web' });
    this.refreshFailure.set(null);
    this.createIngestToken.mockReset().mockResolvedValue({ token: TOKEN, secret: SECRET });
    this.rotateIngestToken.mockReset().mockResolvedValue({
      token: { ...TOKEN, id: 'token-2' },
      secret: ROTATED,
      overlap_ends_at: '2026-10-27T07:30:00Z',
    });
    this.refresh.mockReset().mockImplementation(() => {
      if (this.createIngestToken.mock.calls.length > 0) this.tokens.set([TOKEN]);
      this.refreshFailure.set(null);
      return Promise.resolve();
    });
    provideTokenInventory({
      tokens: this.tokens,
      tokensTruncated: this.tokensTruncated,
      tokensLoaded: this.tokensLoaded,
      tokensRefreshFailure: this.refreshFailure,
      setTokenScope: this.setTokenScope,
      clearTokenScope: this.clearTokenScope,
      refreshTokens: this.refresh,
    });
    await TestBed.configureTestingModule({
      imports: [ConnectCiPage],
      providers: [
        provideZonelessChangeDetection(),
        provideDesktopOnboardingViewport(),
        {
          provide: OnboardingDataService,
          useValue: {
            createIngestToken: this.createIngestToken,
            rotateIngestToken: this.rotateIngestToken,
          },
        },
        { provide: OnboardingStore, useValue: { project: this.project } },
        {
          provide: SessionStore,
          useValue: { has: () => this.permitted(), onSessionEnd: () => () => undefined },
        },
      ],
    }).compileComponents();
    this.fixture = TestBed.createComponent(ConnectCiPage);
    this.fixture.componentRef.setInput('orgId', 'org-1');
    this.fixture.componentRef.setInput('projectId', 'project-1');
    this.fixture.componentRef.setInput('active', active);
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

  readonly submit = async (): Promise<void> => {
    this.root
      .querySelector('[data-testid="connect-token-panel"] form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await this.fixture.whenStable();
  };
}

export function createConnectionPageContext(): ConnectionPageTestContext {
  return new ConnectionPageTestContext();
}
