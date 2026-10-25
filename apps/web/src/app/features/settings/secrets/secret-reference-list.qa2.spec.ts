// QA2 independent gate (09.7.1-T3 AC3): the list is where the reminder chip and the metadata rows
// actually reach the screen, so the 14-day boundary and the empty-scope branch are proved here, not
// only in the pure function. A fixed clock makes the boundary exact.
import { TestBed } from '@angular/core/testing';
import type { IngestTokenView } from '@evidgate/sdk';
import fixtureData from '../../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-token/input.json';
import { ROTATION_CLOCK } from './rotation-reminder-chip.component';
import { SecretReferenceListComponent } from './secret-reference-list.component';

const base = fixtureData.body.items[0] as IngestTokenView;
const NOW = new Date('2026-10-25T00:00:00Z');
const DAY = 24 * 60 * 60 * 1000;

function at(offsetMs: number): string {
  return new Date(NOW.getTime() + offsetMs).toISOString();
}
function row(id: string, patch: Partial<IngestTokenView>): IngestTokenView {
  return { ...base, id, ...patch };
}

describe('SecretReferenceList reminder wiring and metadata branches (09.7.1-T3 QA2)', () => {
  afterEach(() => TestBed.resetTestingModule());

  function mount(rows: readonly IngestTokenView[]): HTMLElement {
    TestBed.configureTestingModule({
      providers: [{ provide: ROTATION_CLOCK, useValue: () => NOW }],
    });
    const fixture = TestBed.createComponent(SecretReferenceListComponent);
    fixture.componentRef.setInput('rows', rows);
    fixture.componentRef.setInput('canWrite', true);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  /** Rows render in input order, so the position names the row without a rotate-button lookup. */
  function reminderAt(root: HTMLElement, index: number): string | null {
    const items = root.querySelectorAll('li[data-secret-reference]');
    return items[index]?.querySelector('[data-reminder]')?.getAttribute('data-reminder') ?? null;
  }

  it('shows expires-soon at exactly 14 days and none one millisecond later', () => {
    const root = mount([
      row('exact', { expires_at: at(14 * DAY) }),
      row('past-window', { expires_at: at(14 * DAY + 1) }),
      row('inside', { expires_at: at(14 * DAY - 1) }),
      row('no-expiry', { expires_at: null }),
    ]);

    expect(reminderAt(root, 0)).toBe('expires-soon');
    expect(reminderAt(root, 1)).toBeNull();
    expect(reminderAt(root, 2)).toBe('expires-soon');
    expect(reminderAt(root, 3)).toBeNull();
    expect(root.querySelectorAll('[data-reminder]')).toHaveLength(2);
  });

  it('shows expired at the expiry instant itself', () => {
    const root = mount([row('now', { expires_at: at(0) }), row('before', { expires_at: at(1) })]);

    expect(reminderAt(root, 0)).toBe('expired');
    expect(reminderAt(root, 1)).toBe('expires-soon');
  });

  it('renders an empty scope list as unavailable instead of an empty cell', () => {
    const root = mount([row('no-scopes', { scopes: [] })]);

    const item = root.querySelector('li[data-secret-reference]');
    expect(item?.textContent).toContain('Unavailable');
    expect(root.querySelector('code.scope')).toBeNull();
  });

  it('renders a server-supplied name and prefix carrying markup as text, creating no element', () => {
    const markup = '<img src=x onerror="globalThis.__qa2ListPwned=1">';
    const root = mount([row('markup', { name: `CI ${markup}`, prefix: `egt_${markup}` })]);

    expect(root.querySelector('img')).toBeNull();
    expect(root.querySelector('[onerror]')).toBeNull();
    expect(Object.hasOwn(globalThis, '__qa2ListPwned')).toBe(false);
    expect(root.textContent).toContain(`CI ${markup}`);
  });
});
