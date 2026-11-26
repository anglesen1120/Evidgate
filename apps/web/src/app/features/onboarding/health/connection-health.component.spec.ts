import { registerLocaleData } from '@angular/common';
import localeVi from '@angular/common/locales/vi';
import { LOCALE_ID, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { IngestTokenView } from '@evidgate/sdk';

import { deriveConnectionHealth } from './connection-health';
import { ConnectionHealthComponent } from './connection-health.component';

registerLocaleData(localeVi);

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

describe.each(['en-US', 'vi'])('ConnectionHealthComponent (%s)', (locale) => {
  let fixture: ComponentFixture<ConnectionHealthComponent>;
  let root: HTMLElement;
  let metadata: Omit<Parameters<typeof deriveConnectionHealth>[0], 'now'>;

  function setMetadata<K extends keyof typeof metadata>(
    name: K,
    value: (typeof metadata)[K],
  ): void {
    metadata = { ...metadata, [name]: value };
    fixture.componentRef.setInput(
      'health',
      deriveConnectionHealth({ ...metadata, now: new Date('2026-10-25T12:00:00Z') }),
    );
    fixture.componentRef.setInput(name, value);
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ConnectionHealthComponent],
      providers: [provideZonelessChangeDetection(), { provide: LOCALE_ID, useValue: locale }],
    }).compileComponents();
    fixture = TestBed.createComponent(ConnectionHealthComponent);
    metadata = { tokens: [], tokensTruncated: false };
    setMetadata('tokens', []);
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  it('keeps the empty health card named, read-only and free of assertive announcements', () => {
    const panel = root.querySelector('section');
    expect(panel?.getAttribute('data-slot')).toBe('card');
    expect(panel?.getAttribute('aria-labelledby')).toBe(root.querySelector('h3')?.id);
    expect(root.textContent).toContain('No connection credentials are recorded.');
    expect(root.querySelector('button, a, [tabindex], [role="alert"]')).toBeNull();
  });

  it('retains credential rows with one named progressbar when a table without a pager refetches', async () => {
    setMetadata('tokens', [TOKEN]);
    fixture.componentRef.setInput('fetching', true);
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(root.querySelectorAll('[role="progressbar"]')).toHaveLength(1);
    });
    expect(root.querySelector('[role="progressbar"]')?.getAttribute('aria-label')).toBe('Loading…');
    expect(root.textContent).toContain(TOKEN.name);
    expect(root.querySelector('table')?.getAttribute('aria-busy')).toBe('true');
    fixture.componentRef.setInput('fetching', false);
    fixture.detectChanges();
    expect(root.querySelector('[role="progressbar"]')).toBeNull();
  });

  it('renders a healthy state with the shared pill, token metadata and usable explanation', async () => {
    setMetadata('tokens', [TOKEN]);
    await fixture.whenStable();
    expect(root.getAttribute('data-testid')).toBe('connection-health');
    expect(root.getAttribute('data-health-state')).toBe('healthy');
    expect(root.querySelector('header app-status-pill')?.textContent).toContain('Healthy');
    expect(root.textContent).toContain('At least one listed credential is usable.');
    const row = root.querySelector('[data-testid="connection-health-credential"]')!;
    expect(row.textContent).toContain(TOKEN.name);
    expect(row.textContent).toContain('Active');
    expect(row.textContent).toContain('Usable.');
  });

  it('renders an unhealthy state and a reason when all credentials are unusable', async () => {
    setMetadata('tokens', [{ ...TOKEN, status: 'revoked' }]);
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

  it('says not recorded without claiming no ingest occurred', async () => {
    setMetadata('tokens', [TOKEN]);
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="connection-health-last-use"]')?.textContent).toContain(
      'Not recorded',
    );
    expect(root.textContent).toContain('Last successful token use (UTC)');
    expect(root.textContent).toContain('at most once every 60 seconds');
    expect(root.textContent).not.toMatch(/nothing was ingested|no successful ingest/i);
  });

  it('shows the truncation caveat and unknown health when nothing usable is shown', async () => {
    setMetadata('tokensTruncated', true);
    await fixture.whenStable();
    expect(root.getAttribute('data-health-state')).toBe('unknown');
    expect(root.textContent).toContain('Only part of the credential list is shown.');
    expect(root.textContent).toContain('an unlisted credential may still be usable');
    expect(root.textContent).toContain('Connection health cannot be determined from this list.');
  });

  it('keeps healthy precedence with truncated lists and qualifies the last-use lower bound', async () => {
    setMetadata('tokens', [{ ...TOKEN, last_used_at: '2026-10-25T13:30:00+02:00' }]);
    setMetadata('tokensTruncated', true);
    await fixture.whenStable();
    expect(root.getAttribute('data-health-state')).toBe('healthy');
    expect(root.textContent).toContain('may be older than the newest use');
    expect(root.querySelector('[data-testid="connection-health-last-use"] time')?.textContent).toBe(
      locale === 'vi' ? '11:30:00 25/10/2026' : '10/25/2026, 11:30:00',
    );
    setMetadata('tokensTruncated', false);
    await fixture.whenStable();
    expect(root.textContent).not.toContain('may be older than the newest use');
    expect(root.textContent).not.toContain('Only part of the credential list is shown.');
  });

  it('keeps the last-use clock and provenance when UTC changes the calendar year', async () => {
    const supplied = '2026-01-01T00:15:04+07:00';
    setMetadata('tokens', [{ ...TOKEN, last_used_at: supplied }]);
    await fixture.whenStable();
    const time = root.querySelector('[data-testid="connection-health-last-use"] time');
    expect(time?.textContent).toBe(
      locale === 'vi' ? '17:15:04 31/12/2025' : '12/31/2025, 17:15:04',
    );
    expect(time?.getAttribute('datetime')).toBe(supplied);
  });

  it('shows expiry and rotation-overlap times in UTC with an explicit usability explanation', async () => {
    setMetadata('tokens', [
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
    expect(rows[0]?.querySelector('time')?.textContent).toBe(
      locale === 'vi' ? '00:00:00 01/01/2020' : '1/1/2020, 00:00:00',
    );
    expect(rows[1]?.textContent).toContain('Usable during the rotation overlap.');
    expect(rows[1]?.textContent).toContain('Rotation overlap ends (UTC):');
    expect(rows[1]?.querySelector('time')?.textContent).toBe(
      locale === 'vi' ? '00:00:00 01/01/2099' : '1/1/2099, 00:00:00',
    );
  });

  it('renders the supplied health snapshot without deriving a different wall-clock state', async () => {
    setMetadata('tokens', [{ ...TOKEN, expires_at: '2020-01-01T00:00:00Z' }]);
    fixture.componentRef.setInput(
      'health',
      deriveConnectionHealth({ ...metadata, now: new Date('2019-01-01T00:00:00Z') }),
    );
    await fixture.whenStable();
    expect(root.getAttribute('data-health-state')).toBe('healthy');
    expect(root.textContent).toContain('Usable.');
  });

  it('renders server labels as text and never renders secret-shaped payload fields', async () => {
    const secret = `egt_${'z'.repeat(48)}`;
    const jwt = ['eyJhbGciOiJIUzI1NiJ9', 'eyJzdWIiOiJwcml2YXRlIn0', 'signature'].join('.');
    const tokenWithExtraFields = { ...TOKEN, name: '<img src=x onerror=alert(1)>', secret, jwt };
    setMetadata('tokens', [tokenWithExtraFields]);
    await fixture.whenStable();
    expect(root.textContent).toContain('<img src=x onerror=alert(1)> (public-prefix)');
    expect(root.querySelector('img, script')).toBeNull();
    for (const element of [root, ...root.querySelectorAll('*')]) {
      expect(element.textContent).not.toMatch(
        /egt_[A-Za-z0-9_-]{32,}|eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/,
      );
      expect(element.outerHTML).not.toContain(secret);
      expect(element.outerHTML).not.toContain(jwt);
    }
  });
});
