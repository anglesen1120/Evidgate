import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import type { AuditEntryDto, AuditListResponse } from '@evidgate/sdk';
import { AuditDataService } from '../../../core/data/audit-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { AuditPage } from './audit.page';
import { AuditTableComponent } from './audit-table.component';

provideLayoutTestStubs();

function row(index: number, createdAt = '2026-10-25T00:00:00Z'): AuditEntryDto {
  return {
    id: `event-${index}`,
    seq: index,
    actor_type: 'user',
    actor_id: 'member-1',
    action: 'auth.login',
    prev_hash: 'previous',
    entry_hash: 'entry',
    created_at: createdAt,
  };
}

function required<T extends Element>(root: HTMLElement, selector: string): T {
  const element = root.querySelector<T>(selector);
  if (!element) throw new Error(`Missing ${selector}`);
  return element;
}

describe('AuditPage boundary browsing and export scope', () => {
  const org = signal<{ id: string } | null>({ id: 'org-1' });
  const list = vi.fn<AuditDataService['list']>();

  beforeEach(() => {
    org.set({ id: 'org-1' });
    list.mockReset();
    TestBed.configureTestingModule({
      imports: [AuditPage],
      providers: [
        provideZonelessChangeDetection(),
        { provide: AuditDataService, useValue: { list } },
        { provide: SessionStore, useValue: { currentOrg: org } },
        { provide: ActivatedRoute, useValue: { snapshot: { data: { pageHeadingLevel: 1 } } } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  it.each([0, 1, 24, 25, 26, 49, 50, 51])(
    'retains every row exactly once across bounded pages for %s events',
    async (count) => {
      const entries = Array.from({ length: count }, (_, index) =>
        row(index, new Date(Date.UTC(2026, 9, 25, 0, 0, index)).toISOString()),
      );
      list.mockImplementation((_org, _query, cursor) => {
        const start = Number(cursor ?? 0);
        return Promise.resolve<AuditListResponse>({
          items: entries.slice(start, start + 50),
          next_cursor: start + 50 < count ? String(start + 50) : null,
        });
      });
      const fixture = TestBed.createComponent(AuditPage);
      await fixture.whenStable();
      const root = fixture.nativeElement as HTMLElement;
      if (count === 0) {
        expect(root.querySelector('[data-audit-row]')).toBeNull();
        expect(root.textContent).toContain('No activity recorded');
        return;
      }
      const seen: string[] = [];
      for (let page = 0; page < Math.ceil(count / 25); page++) {
        seen.push(
          ...Array.from(root.querySelectorAll('[data-audit-row] time')).map(
            (time) => time.getAttribute('datetime') ?? '',
          ),
        );
        const expected = entries.slice(page * 25, page * 25 + 25).length;
        expect(root.querySelectorAll('[data-audit-row]')).toHaveLength(expected);
        expect(root.querySelector('[data-audit-row-range]')?.textContent).toBe(
          `Rows ${page * 25 + 1}–${Math.min(page * 25 + 25, count > 50 && page < 2 ? 50 : count)} of ${count > 50 && page < 2 ? 50 : count} loaded.`,
        );
        if (page + 1 < Math.ceil(count / 25)) {
          if (page === 1) {
            required<HTMLButtonElement>(root, '[data-audit-load-more]').click();
            await fixture.whenStable();
          }
          required<HTMLButtonElement>(root, '[data-audit-next]').click();
          await fixture.whenStable();
        }
      }
      expect(seen).toEqual(entries.map((event) => event.created_at));
      expect(required<HTMLButtonElement>(root, '[data-audit-next]').disabled).toBe(true);
    },
  );

  it('blocks edited export scope until Apply and enables again after reverting the draft', async () => {
    list.mockResolvedValue({ items: [row(1)], next_cursor: null });
    const fixture = TestBed.createComponent(AuditPage);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const action = required<HTMLInputElement>(root, '[name="action"]');
    const exportButton = required<HTMLButtonElement>(root, '[data-audit-export]');
    expect(exportButton.disabled).toBe(false);
    action.value = 'run.create';
    action.dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
    expect(exportButton.disabled).toBe(true);
    expect(root.querySelector('#audit-export-draft-hint')).not.toBeNull();
    exportButton.click();
    expect(list).toHaveBeenCalledTimes(1);
    action.value = '';
    action.dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
    expect(exportButton.disabled).toBe(false);
    expect(root.querySelector('#audit-export-draft-hint')).toBeNull();
  });
});

describe('AuditTableComponent invalid timestamp boundary', () => {
  afterEach(() => TestBed.resetTestingModule());

  it.each(['not-a-date', '2026-99-99T25:99:99Z'])(
    'keeps the activity readable for malformed timestamp %s',
    async (timestamp) => {
      TestBed.configureTestingModule({
        imports: [AuditTableComponent],
        providers: [provideZonelessChangeDetection()],
      });
      const fixture = TestBed.createComponent(AuditTableComponent);
      fixture.componentRef.setInput('rows', [row(1, timestamp)]);
      await fixture.whenStable();
      expect((fixture.nativeElement as HTMLElement).textContent).toContain('auth.login');
    },
  );
});
