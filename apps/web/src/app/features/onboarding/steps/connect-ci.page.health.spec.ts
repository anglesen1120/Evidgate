import { registerLocaleData } from '@angular/common';
import localeVi from '@angular/common/locales/vi';
import { LOCALE_ID } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import type { IngestTokenView } from '@evidgate/sdk';

import type { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import type { ConnectCiPage } from './connect-ci.page';
import {
  BINDING,
  TOKEN,
  CHALLENGE,
  SECRET,
  createConnectionPageContext,
} from './connect-ci.page.test-support';

registerLocaleData(localeVi);

describe.each(['en-US', 'vi'])('ConnectCiPage health and CLI composition (%s)', (locale) => {
  const context = createConnectionPageContext();
  const {
    bindings,
    tokens,
    canGoNext,
    createCiBinding,
    createIngestToken,
    rotateIngestToken,
    refresh,
    fill,
    submit,
  } = context;
  let fixture: ComponentFixture<ConnectCiPage>;
  let root: HTMLElement;
  let slot: RevealSlot;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [{ provide: LOCALE_ID, useValue: locale }] });
    await context.mount();
    fixture = context.fixture;
    root = context.root;
    slot = context.slot;
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

  it('renders credential creation through the shared calendar with seconds and raw provenance', async () => {
    const supplied = '2026-01-01T00:15:04+07:00';
    bindings.set([{ ...BINDING, created_at: supplied }]);
    await fixture.whenStable();
    const time = root.querySelector(`time[datetime="${supplied}"]`);
    expect(time?.textContent).toBe(
      locale === 'vi' ? '17:15:04 31/12/2025' : '12/31/2025, 5:15:04\u202fPM',
    );
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
