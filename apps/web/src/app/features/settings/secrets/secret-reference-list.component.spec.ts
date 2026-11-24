import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { clearTranslations, loadTranslations } from '@angular/localize';
import type { IngestTokenView } from '@evidgate/sdk';
import fixtureData from '../../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-token/input.json';
import { SecretReferenceListComponent } from './secret-reference-list.component';
import { ROTATION_CLOCK } from './rotation-reminder-chip.component';
import { SECRET_DESKTOP_PROVIDER } from './secrets-page.testing';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';

const base = fixtureData.body.items[0] as IngestTokenView;
class SecretActionsHarness extends ComponentHarness {
  static hostSelector = 'app-secret-reference-list';
  readonly inspect = this.locatorFor('button[data-secret-inspect]');
  readonly rotate = this.locatorFor('button[data-rotate]');
  readonly icons = this.locatorForAll('button[appRowAction] hlm-icon');
}
describe('SecretReferenceListComponent metadata boundary (AC3)', () => {
  provideLayoutTestStubs();
  beforeEach(() => TestBed.configureTestingModule({ providers: [SECRET_DESKTOP_PROVIDER] }));
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
    const pills = (fixture.nativeElement as HTMLElement).querySelectorAll(
      '[data-secret-reference] .status-pill__label',
    );
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
      Array.from(root.querySelectorAll('[data-secret-reference] .status-pill__label'), (pill) =>
        pill.textContent?.trim(),
      ),
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
  it('renders 20 seeded metadata rows but never any decoy full-length value, including attributes', async () => {
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
    const inspect = root.querySelectorAll<HTMLButtonElement>('[data-secret-inspect]');
    for (const [index, row] of rows.entries()) {
      expect(root.outerHTML).not.toContain(row.secret);
      expect(root.textContent).toContain(row.name);
      expect(root.textContent).toContain(row.prefix);
      inspect[index]?.click();
      await fixture.whenStable();
      const detail = document.querySelector('[role="dialog"]');
      expect(detail?.outerHTML).not.toContain(row.secret);
      for (const scope of row.scopes) expect(detail?.textContent).toContain(scope);
      expect(detail?.querySelector('time')?.getAttribute('datetime')).toBeTruthy();
      detail?.querySelector<HTMLButtonElement>('button[hlmDialogClose]')?.click();
      await fixture.whenStable();
    }
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
  it('keeps icon-only inspection and rotation native and blocks rotation while busy or refused', async () => {
    const fixture = TestBed.createComponent(SecretReferenceListComponent);
    fixture.componentRef.setInput('rows', [{ ...base, name: 'Long reference '.repeat(20) }]);
    fixture.componentRef.setInput('canWrite', true);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      SecretActionsHarness,
    );
    const button = await harness.rotate();
    const inspect = await harness.inspect();
    expect(await inspect.text()).toBe('');
    expect(await inspect.getAttribute('aria-label')).toBe(
      `Inspect reference ${'Long reference '.repeat(20)}`,
    );
    expect(await button.text()).toBe('');
    expect(await button.getAttribute('aria-label')).toBe(
      `Rotate credential ${'Long reference '.repeat(20)}`,
    );
    expect(await button.hasClass('row-action-danger')).toBe(true);
    const icons = await harness.icons();
    expect(icons).toHaveLength(2);
    for (const icon of icons) expect(await icon.getAttribute('aria-hidden')).toBe('true');
    const rotate = vi.fn();
    fixture.componentInstance.rotate.subscribe(rotate);
    expect(root.querySelector('[data-secret-reference]')?.getAttribute('data-slot')).toBe(
      'table-row',
    );
    expect(await button.getProperty('type')).toBe('button');
    expect(root.querySelector('[data-secret-reference] h3')?.textContent).toBe(
      'Long reference '.repeat(20),
    );
    await button.focus();
    expect(await button.isFocused()).toBe(true);
    await button.click();
    expect(rotate).toHaveBeenCalledOnce();
    fixture.componentRef.setInput('busy', true);
    fixture.componentRef.setInput('busyId', base.id);
    await fixture.whenStable();
    expect(await button.getProperty('disabled')).toBe(true);
    expect(await button.getAttribute('aria-busy')).toBe('true');
    await button.click();
    expect(rotate).toHaveBeenCalledOnce();
    fixture.componentRef.setInput('busy', false);
    fixture.componentRef.setInput('blocked', true);
    await fixture.whenStable();
    expect(await button.getProperty('disabled')).toBe(true);
    await button.click();
    expect(rotate).toHaveBeenCalledOnce();
  });
});
