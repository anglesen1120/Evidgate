import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { CiBindingView, IngestTokenView } from '@evidgate/sdk';

import { ConnectionHealthComponent } from './connection-health.component';

const BINDING: CiBindingView = {
  id: 'binding-1',
  project_id: 'project-1',
  provider: 'github_actions',
  repository_id: 123,
  repository_full_name: 'acme/storefront',
  allowed_ref_patterns: ['refs/heads/main'],
  allowed_workflow_refs: [],
  status: 'active',
  activated_at: null,
  created_at: '2026-10-25T00:00:00Z',
  version: 1,
};
const TOKEN: IngestTokenView = {
  id: 'token-1',
  project_id: 'project-1',
  name: 'CI uploader',
  prefix: 'public-prefix',
  status: 'active',
  scopes: [],
  expires_at: null,
  overlap_ends_at: null,
  last_used_at: null,
  created_at: '2026-10-25T00:00:00Z',
};

describe('ConnectionHealthComponent', () => {
  let fixture: ComponentFixture<ConnectionHealthComponent>;
  let root: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ConnectionHealthComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(ConnectionHealthComponent);
    fixture.componentRef.setInput('bindings', []);
    fixture.componentRef.setInput('tokens', []);
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  it('renders a healthy state with the shared pill, repository and usable explanation', async () => {
    fixture.componentRef.setInput('bindings', [BINDING]);
    await fixture.whenStable();
    expect(root.getAttribute('data-testid')).toBe('connection-health');
    expect(root.getAttribute('data-health-state')).toBe('healthy');
    expect(root.querySelector('header app-status-pill')?.textContent).toContain('Healthy');
    expect(root.textContent).toContain('At least one listed credential is usable.');
    const row = root.querySelector('[data-testid="connection-health-credential"]')!;
    expect(row.textContent).toContain('acme/storefront');
    expect(row.textContent).toContain('Active');
    expect(row.textContent).toContain('Usable.');
  });

  it('renders an unhealthy state and a reason when all credentials are unusable', async () => {
    fixture.componentRef.setInput('tokens', [{ ...TOKEN, status: 'revoked' }]);
    await fixture.whenStable();
    expect(root.getAttribute('data-health-state')).toBe('unhealthy');
    expect(root.querySelector('header app-status-pill')?.textContent).toContain('Unhealthy');
    expect(root.textContent).toContain('None of the listed credentials is usable.');
    const row = root.querySelector('[data-testid="connection-health-credential"]')!;
    expect(row.textContent).toContain('CI uploader (public-prefix)');
    expect(row.textContent).toContain('Revoked');
    expect(row.textContent).toContain('Not usable: the token has been revoked.');
  });

  it('uses an h3 within a named section', () => {
    const section = root.querySelector('section')!;
    const heading = root.querySelector('h3')!;
    expect(section.getAttribute('aria-labelledby')).toBe(heading.id);
    expect(heading.textContent).toBe('Connection health');
    expect(root.querySelector('h1, h2, h4')).toBeNull();
  });

  it('says not recorded without claiming no ingest occurred, including OIDC-only projects', async () => {
    fixture.componentRef.setInput('bindings', [BINDING]);
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="connection-health-last-use"]')?.textContent).toContain(
      'Not recorded',
    );
    expect(root.textContent).toContain('Last successful token use (UTC)');
    expect(root.textContent).toContain('OIDC-only activity is not shown');
    expect(root.textContent).toContain('at most once every 60 seconds');
    expect(root.textContent).not.toMatch(/nothing was ingested|no successful ingest/i);
  });

  it.each(['bindingsTruncated', 'tokensTruncated'])(
    'shows the truncation caveat for %s and unknown health when nothing usable is shown',
    async (flag) => {
      fixture.componentRef.setInput(flag, true);
      await fixture.whenStable();
      expect(root.getAttribute('data-health-state')).toBe('unknown');
      expect(root.textContent).toContain('Only part of the credential list is shown.');
      expect(root.textContent).toContain('an unlisted credential may still be usable');
      expect(root.textContent).toContain('Connection health cannot be determined from this list.');
    },
  );

  it('keeps healthy precedence with truncated lists and qualifies the last-use lower bound', async () => {
    fixture.componentRef.setInput('tokens', [
      { ...TOKEN, last_used_at: '2026-10-25T13:30:00+02:00' },
    ]);
    fixture.componentRef.setInput('tokensTruncated', true);
    await fixture.whenStable();
    expect(root.getAttribute('data-health-state')).toBe('healthy');
    expect(root.textContent).toContain('may be older than the newest use');
    expect(root.querySelector('[data-testid="connection-health-last-use"] time')?.textContent).toBe(
      '2026-10-25 11:30:00',
    );
    fixture.componentRef.setInput('tokensTruncated', false);
    fixture.componentRef.setInput('bindingsTruncated', true);
    await fixture.whenStable();
    expect(root.textContent).not.toContain('may be older than the newest use');
    expect(root.textContent).toContain('Only part of the credential list is shown.');
  });

  it('renders pending bindings as awaiting verification rather than healthy', async () => {
    fixture.componentRef.setInput('bindings', [{ ...BINDING, status: 'pending_verification' }]);
    await fixture.whenStable();
    expect(root.getAttribute('data-health-state')).toBe('pending');
    expect(root.textContent).toContain('Pending verification');
    expect(root.textContent).toContain(
      'Not usable until the first CI exchange verifies this binding.',
    );
  });

  it('shows expiry and rotation-overlap times in UTC with an explicit usability explanation', async () => {
    fixture.componentRef.setInput('tokens', [
      { ...TOKEN, expires_at: '2020-01-01T02:00:00+02:00' },
      {
        ...TOKEN,
        id: 'rotating',
        status: 'rotated',
        overlap_ends_at: '2099-01-01T02:00:00+02:00',
      },
    ]);
    await fixture.whenStable();
    const rows = root.querySelectorAll('[data-testid="connection-health-credential"]');
    expect(rows).toHaveLength(2);
    expect(rows[0]?.textContent).toContain('Not usable: the token has expired.');
    expect(rows[0]?.textContent).toContain('Expires (UTC):');
    expect(rows[0]?.querySelector('time')?.textContent).toBe('2020-01-01 00:00:00');
    expect(rows[1]?.textContent).toContain('Usable during the rotation overlap.');
    expect(rows[1]?.textContent).toContain('Rotation overlap ends (UTC):');
    expect(rows[1]?.querySelector('time')?.textContent).toBe('2099-01-01 00:00:00');
  });

  it('renders server labels as text and never renders secret-shaped payload fields', async () => {
    const secret = `egt_${'z'.repeat(48)}`;
    const challenge = 'private-binding-challenge';
    const jwt = ['eyJhbGciOiJIUzI1NiJ9', 'eyJzdWIiOiJwcml2YXRlIn0', 'signature'].join('.');
    fixture.componentRef.setInput('tokens', [
      { ...TOKEN, name: '<img src=x onerror=alert(1)>', secret, jwt },
    ]);
    fixture.componentRef.setInput('bindings', [{ ...BINDING, binding_challenge: challenge }]);
    await fixture.whenStable();
    expect(root.textContent).toContain('<img src=x onerror=alert(1)> (public-prefix)');
    expect(root.querySelector('img, script')).toBeNull();
    for (const element of [root, ...root.querySelectorAll('*')]) {
      expect(element.textContent).not.toMatch(
        /egt_[A-Za-z0-9_-]{32,}|eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/,
      );
      expect(element.outerHTML).not.toContain(secret);
      expect(element.outerHTML).not.toContain(challenge);
      expect(element.outerHTML).not.toContain(jwt);
    }
  });
});
