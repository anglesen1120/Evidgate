import { TestBed } from '@angular/core/testing';
import { clearTranslations, loadTranslations } from '@angular/localize';
import type { IngestTokenView } from '@evidgate/sdk';
import fixtureData from '../../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-token/input.json';
import { SecretReferenceListComponent } from './secret-reference-list.component';
import { ROTATION_CLOCK } from './rotation-reminder-chip.component';

const base = fixtureData.body.items[0] as IngestTokenView;
describe('SecretReferenceListComponent metadata boundary (AC3)', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    clearTranslations();
  });
  it('localizes every known rotation status and preserves an unknown future wire value', () => {
    const fixture = TestBed.createComponent(SecretReferenceListComponent);
    fixture.componentRef.setInput(
      'rows',
      ['active', 'rotated', 'revoked', 'expired', 'future-status'].map((status) => ({
        ...base,
        id: status,
        status,
      })),
    );
    fixture.detectChanges();
    const pills = (fixture.nativeElement as HTMLElement).querySelectorAll('.status-pill__label');
    expect(Array.from(pills, (pill) => pill.textContent?.trim())).toEqual([
      'Active',
      'Rotated',
      'Revoked',
      'Expired',
      'future-status',
    ]);
  });
  it('renders translated Vietnamese statuses and a token-specific Rotate accessible name', () => {
    loadTranslations({
      'settings.secrets.status-active': 'Đang hoạt động',
      'settings.secrets.status-rotated': 'Đã xoay',
      'settings.secrets.status-revoked': 'Đã thu hồi',
      'settings.secrets.status-expired': 'Đã hết hạn',
      'settings.secrets.rotate-named': 'Xoay thông tin xác thực {$name}',
    });
    const fixture = TestBed.createComponent(SecretReferenceListComponent);
    fixture.componentRef.setInput(
      'rows',
      ['active', 'rotated', 'revoked', 'expired'].map((status) => ({
        ...base,
        id: status,
        name: 'CI sản xuất',
        status,
      })),
    );
    fixture.componentRef.setInput('canWrite', true);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(
      Array.from(root.querySelectorAll('.status-pill__label'), (pill) => pill.textContent?.trim()),
    ).toEqual(['Đang hoạt động', 'Đã xoay', 'Đã thu hồi', 'Đã hết hạn']);
    expect(root.querySelector('[data-rotate]')?.getAttribute('aria-label')).toBe(
      'Xoay thông tin xác thực CI sản xuất',
    );
  });
  it('identifies the credential in each Rotate accessible name, escaping markup as text', () => {
    const fixture = TestBed.createComponent(SecretReferenceListComponent);
    const names = ['Storefront CI', 'API <strong>production</strong>'];
    fixture.componentRef.setInput(
      'rows',
      names.map((name, index) => ({ ...base, id: `named-${index}`, name })),
    );
    fixture.componentRef.setInput('canWrite', true);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(
      Array.from(root.querySelectorAll('[data-rotate]'), (button) =>
        button.getAttribute('aria-label'),
      ),
    ).toEqual(names.map((name) => `Rotate credential ${name}`));
    expect(root.querySelector('strong')).toBeNull();
  });
  it('renders 20 seeded metadata rows but never any decoy full-length value, including attributes', () => {
    TestBed.configureTestingModule({
      providers: [{ provide: ROTATION_CLOCK, useValue: () => new Date('2026-10-25T00:00:00Z') }],
    });
    const fixture = TestBed.createComponent(SecretReferenceListComponent);
    const rows = Array.from({ length: 20 }, (_, index) => ({
      ...base,
      id: `ref-${index}`,
      name: `CI reference ${index}`,
      prefix: `eg_ci_${index}`,
      secret: `FULL_CREDENTIAL_${index}_${'z'.repeat(70)}`,
    }));
    fixture.componentRef.setInput('rows', rows);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('[data-secret-reference]')).toHaveLength(20);
    for (const row of rows) {
      expect(root.outerHTML).not.toContain(row.secret);
      expect(root.textContent).toContain(row.name);
      expect(root.textContent).toContain(row.prefix);
      for (const scope of row.scopes) expect(root.textContent).toContain(scope);
    }
    expect(root.querySelector('time')?.getAttribute('datetime')).toBeTruthy();
    expect(root.querySelector('[data-rotate]')).toBeNull();
  });
  it('offers Rotate only for active rows when write permission is supplied', () => {
    const fixture = TestBed.createComponent(SecretReferenceListComponent);
    fixture.componentRef.setInput(
      'rows',
      ['active', 'rotated', 'expired', 'revoked'].map((status) => ({
        ...base,
        id: status,
        status,
      })),
    );
    fixture.componentRef.setInput('canWrite', true);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('[data-rotate]')).toHaveLength(
      1,
    );
    fixture.componentRef.setInput('canWrite', false);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-rotate]')).toBeNull();
  });
  it('keeps the Helm rotation action keyboard-native and blocks activation while busy', () => {
    const fixture = TestBed.createComponent(SecretReferenceListComponent);
    fixture.componentRef.setInput('rows', [{ ...base, name: 'Long reference '.repeat(20) }]);
    fixture.componentRef.setInput('canWrite', true);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const button = root.querySelector<HTMLButtonElement>('[data-rotate]');
    const rotate = vi.fn();
    fixture.componentInstance.rotate.subscribe(rotate);
    expect(root.querySelector('[data-secret-reference]')?.getAttribute('data-slot')).toBe('card');
    expect(button?.getAttribute('data-slot')).toBe('button');
    expect(button?.type).toBe('button');
    expect(root.querySelector('h3')?.textContent).toBe('Long reference '.repeat(20));
    button?.focus();
    expect(document.activeElement).toBe(button);
    button?.click();
    expect(rotate).toHaveBeenCalledOnce();
    fixture.componentRef.setInput('busy', true);
    fixture.componentRef.setInput('busyId', base.id);
    fixture.detectChanges();
    expect(button?.disabled).toBe(true);
    expect(button?.getAttribute('aria-busy')).toBe('true');
    button?.click();
    expect(rotate).toHaveBeenCalledOnce();
  });
});
