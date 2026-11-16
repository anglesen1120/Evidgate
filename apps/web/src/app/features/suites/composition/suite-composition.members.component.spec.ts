// The member cases of the composition screen: the chunked flat table's keyboard activation and the
// note that counts the members a repeated variant cell cannot draw. They share the route, the
// TestBed host and the fixtures with `suite-composition.component.spec.ts`, which keeps the matrix,
// strategy and state cases.
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import type { SuiteRevisionDto } from '@evidgate/sdk';
import { SuitesDataService } from '../../../core/data/suites-data.service';
import { routes } from '../suites.routes';
import { SuiteCompositionComponent } from './suite-composition.component';
import {
  gridRevision,
  readyRevision as revision,
  sparseGridRevision,
} from './suite-composition.testing';

// Applies to every test in this file. Idle wall of the slowest is 190 ms; the smoke run hit the
// 5000 ms default here after the fuzz stage (CPU contention), so 15 s keeps a ~1.3x margin only (11.3 s of 15 s under smoke load).
describe('SuiteCompositionComponent members', { timeout: 15_000 }, () => {
  const getRevision = vi.fn<(id: string) => Promise<SuiteRevisionDto>>();
  beforeEach(() => {
    getRevision.mockReset().mockResolvedValue(revision);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: 'projects/:projectId/suites', children: routes }]),
        { provide: SuitesDataService, useValue: { getRevision } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  async function open(id = revision.id) {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(
      `/projects/project-1/suites/revisions/${id}`,
      SuiteCompositionComponent,
    );
    await harness.fixture.whenStable();
    const root = harness.routeNativeElement;
    if (!root) throw new Error('The composition route did not render');
    return { harness, root };
  }

  it('is activated by Enter and Space, which the page never captures', async () => {
    getRevision.mockResolvedValue(sparseGridRevision(61, 41, 201));
    const { root } = await open();
    const rows = () => root.querySelectorAll('[data-testid="suite-members-flat"] tbody tr');
    const more = root.querySelector<HTMLButtonElement>('[data-testid="suite-members-more"]');
    if (more === null) throw new Error('The chunked member list rendered no Show more control');
    // A native button is what makes the keyboard activation the browser's job: it turns Enter and
    // Space into a click itself, so any key handler here would fire the press twice.
    expect(more.tagName).toBe('BUTTON');
    expect(more.type).toBe('button');
    more.focus();
    expect(document.activeElement).toBe(more);

    for (const key of ['Enter', ' ']) {
      const keydown = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
      // jsdom runs no default action for a key press, so this asserts the half that belongs to the
      // page: neither key is swallowed, which is what lets the browser complete the activation.
      const notCaptured = more.dispatchEvent(keydown);
      expect(notCaptured, `${key} reaches the control`).toBe(true);
      expect(keydown.defaultPrevented, `${key} is not cancelled`).toBe(false);
    }
    expect(rows()).toHaveLength(200);
  });

  it('says how many members a repeated variant cell hides, and keeps the first one', async () => {
    const first = revision.members.at(0);
    if (first === undefined) throw new Error('The served fixture no longer holds a member');
    const repeated = { ...first, id: '018f0000-0000-7000-8000-00000000c3fe', ordinal: 9 };
    getRevision.mockResolvedValue({ ...revision, members: [...revision.members, repeated] });
    const { root } = await open();
    const note = root.querySelector('[data-testid="suite-duplicate-members"]');

    // The note is on the screen, not in the console: the revision served a member the matrix has
    // no cell for, and a reader has to be able to see that.
    expect(note?.textContent?.trim()).toBe(
      'Repeated variant members: 1; the matrix keeps only the first member of each cell.',
    );
    const drawn = [...root.querySelectorAll('[data-suite-member-id]')].map(
      (cell) => cell.textContent,
    );
    expect(drawn).toContain(first.id);
    expect(drawn).not.toContain(repeated.id);
    expect(drawn).toHaveLength(revision.members.length);
  });

  it('shows no repeated-cell note for a revision whose cells are all distinct', async () => {
    const { root } = await open();

    expect(root.querySelector('[data-testid="suite-duplicate-members"]')).toBeNull();
  });

  it('counts the members a cell with three members hides, not the one cell that repeats', async () => {
    const first = revision.members.at(0);
    if (first === undefined) throw new Error('The served fixture no longer holds a member');
    const extras = [
      '018f0000-0000-7000-8000-00000000c3fc',
      '018f0000-0000-7000-8000-00000000c3fd',
    ].map((id, index) => ({ ...first, id, ordinal: 8 + index }));
    getRevision.mockResolvedValue({ ...revision, members: [...revision.members, ...extras] });
    const { root } = await open();
    const note = root.querySelector('[data-testid="suite-duplicate-members"]');

    // One cell holds three members, so the note names the two the matrix cannot draw: labelling
    // the count as cells would read `1` here and hide that a second member was served.
    expect(note?.textContent?.trim()).toBe(
      'Repeated variant members: 2; the matrix keeps only the first member of each cell.',
    );
    const drawn = [...root.querySelectorAll('[data-suite-member-id]')].map(
      (cell) => cell.textContent,
    );
    expect(drawn).toContain(first.id);
    for (const extra of extras) expect(drawn).not.toContain(extra.id);
  });

  it('claims no hidden member in the flat layout, which lists every served member', async () => {
    const base = sparseGridRevision(61, 41, 201);
    const first = base.members.at(0);
    if (first === undefined) throw new Error('The generated grid holds no member');
    getRevision.mockResolvedValue({
      ...base,
      members: [...base.members, { ...first, id: '018f0000-0000-7000-8000-00000000c3fd' }],
    });
    const { root } = await open();

    // The pair still collapses to one cell, but the flat table draws a row per served member, so a
    // note saying the matrix keeps the first would be the only place the second member is missing.
    expect(root.querySelector('[data-testid="suite-members-flat"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="suite-duplicate-members"]')).toBeNull();
  });

  it('preserves flat-table roles and accessible cell labels in the mobile card layout', async () => {
    getRevision.mockResolvedValue(sparseGridRevision(61, 41, 201));
    const { root } = await open();
    const table = root.querySelector('[data-testid="suite-members-flat"]');
    if (!table) throw new Error('Expected the flat member table');

    expect(table.getAttribute('role')).toBe('table');
    expect(table.getAttribute('aria-labelledby')).toBe('suite-matrix-heading');
    expect(table.querySelectorAll('[role="rowgroup"]')).toHaveLength(2);
    expect(table.querySelector('thead')?.classList.contains('max-md:sr-only')).toBe(true);
    const headers = [...table.querySelectorAll('thead th')];
    expect(headers).toHaveLength(5);
    for (const header of headers) {
      expect(header.getAttribute('role')).toBe('columnheader');
      expect(header.getAttribute('scope')).toBe('col');
      expect(header.closest('[aria-hidden="true"], [hidden]')).toBeNull();
    }
    expect(table.querySelector('thead tr')?.getAttribute('role')).toBe('row');
    const rows = [...table.querySelectorAll('tbody tr')];
    expect(rows).toHaveLength(200);
    for (const row of rows) {
      expect(row.getAttribute('role')).toBe('row');
      const cells = [...row.children];
      expect(cells.map((cell) => cell.getAttribute('role'))).toEqual([
        'rowheader',
        'cell',
        'cell',
        'cell',
        'cell',
      ]);
      expect(cells[0]?.getAttribute('scope')).toBe('row');
      for (const index of [0, 1, 3, 4]) {
        const label = cells[index]?.querySelector('span');
        expect(label?.textContent?.trim()).toBe(headers[index]?.textContent?.trim());
        expect(label?.classList.contains('md:hidden')).toBe(true);
        expect(label?.closest('[aria-hidden="true"], [hidden]')).toBeNull();
      }
      expect(cells[2]?.textContent?.trim()).toMatch(/^(Required|Optional)$/);
    }
  });

  it('provides labelled mobile records with the complete supplied variant hashes', async () => {
    const { root } = await open();
    const mobile = root.querySelector('[data-testid="suite-members-mobile"]');
    expect(mobile?.getAttribute('role')).toBe('list');
    expect(mobile?.classList.contains('md:hidden')).toBe(true);
    expect(mobile?.querySelectorAll('li')).toHaveLength(revision.members.length);
    for (const member of revision.members) {
      expect(mobile?.textContent).toContain(member.id);
      expect(mobile?.textContent).toContain(member.case_revision_id);
      expect(mobile?.textContent).toContain(member.variant_hash);
    }
    expect(mobile?.textContent).toContain('Variant hash');
    for (const record of mobile?.querySelectorAll('li') ?? []) {
      const terms = [...record.querySelectorAll('dt')];
      expect(terms).toHaveLength(5);
      for (const term of terms) {
        expect(term.textContent?.trim()).not.toBe('');
        expect(term.closest('[aria-hidden="true"], [hidden]')).toBeNull();
        expect(term.nextElementSibling?.tagName).toBe('DD');
      }
    }
    expect(root.querySelector('[data-testid="suite-matrix"]')?.getAttribute('data-slot')).toBe(
      'table',
    );
  });

  it('pages mobile records without losing focus or repeating the final page', async () => {
    getRevision.mockResolvedValue(gridRevision(15, 15));
    const { root, harness } = await open();
    const mobile = root.querySelector('[data-testid="suite-members-mobile"]');
    const more = root.querySelector<HTMLButtonElement>('[data-testid="suite-mobile-more"]');
    expect(mobile?.querySelectorAll('li')).toHaveLength(200);
    expect(more?.getAttribute('data-slot')).toBe('button');
    more?.focus();
    more?.click();
    harness.fixture.detectChanges();
    expect(mobile?.querySelectorAll('li')).toHaveLength(225);
    expect(more?.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(more);
    more?.click();
    harness.fixture.detectChanges();
    expect(mobile?.querySelectorAll('li')).toHaveLength(225);
  });
});
