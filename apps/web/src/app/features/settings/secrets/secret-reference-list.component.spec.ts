import { TestBed } from '@angular/core/testing';
import type { IngestTokenView } from '@evidgate/sdk';
import fixtureData from '../../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-token/input.json';
import { SecretReferenceListComponent } from './secret-reference-list.component';
import { ROTATION_CLOCK } from './rotation-reminder-chip.component';

const base = fixtureData.body.items[0] as IngestTokenView;
describe('SecretReferenceListComponent metadata boundary (AC3)', () => {
  afterEach(() => TestBed.resetTestingModule());
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
});
