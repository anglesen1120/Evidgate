import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import type { SuiteRevisionDto } from '@evidgate/sdk';
import ready from '../../../../../../../fixtures/web-api/suite-revision/input.json';
import empty from '../../../../../../../fixtures/web-api/suite-revision-empty/input.json';
import forbidden from '../../../../../../../fixtures/web-api/suite-revision-forbidden/input.json';
import notFound from '../../../../../../../fixtures/web-api/suite-revision-not-found/input.json';
import published from '../../../../../../../fixtures/web-api/suite-revision-published/input.json';
import unavailable from '../../../../../../../fixtures/web-api/suite-revision-unavailable/input.json';
import { SuitesDataService } from '../../../core/data/suites-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { mapApiError } from '../../../core/http/error-mapper';
import { routes } from '../suites.routes';
import { SuiteCompositionComponent } from './suite-composition.component';

const revision = ready.body as SuiteRevisionDto;
const publishedRevision = published.body as SuiteRevisionDto;
const emptyRevision = empty.body as SuiteRevisionDto;
const editAffordances =
  'button, input, select, textarea, [contenteditable]:not([contenteditable="false"]), ' +
  '[role="button"], [role="textbox"], [role="combobox"], [role="slider"], [role="spinbutton"], ' +
  '[role="checkbox"], [role="switch"]';

/** A revision whose members fill a rows × columns grid, one member per cell. */
function gridRevision(rows: number, columns: number): SuiteRevisionDto {
  const members: SuiteRevisionDto['members'] = [];
  for (let row = 0; row < rows; row += 1) {
    for (let column = 0; column < columns; column += 1) {
      members.push({
        id: `member-${row}-${column}`,
        case_revision_id: `case-${String(row).padStart(3, '0')}`,
        variant_hash: `variant-${column}`,
        params: { column },
        required: column % 2 === 0,
        ordinal: column,
      });
    }
  }
  return { ...revision, members, edges: [] };
}

/**
 * A revision whose grid has `rows × columns` cells but only `placed` members, so the layout is
 * `flat` while the member list stays short enough to exhaust in one press. The stride hits every
 * row and every column while `placed` stays below their product, which is what makes the cell
 * count — not the member count — the reason for the flat layout.
 */
function sparseGridRevision(rows: number, columns: number, placed: number): SuiteRevisionDto {
  const members: SuiteRevisionDto['members'] = [];
  for (let index = 0; index < placed; index += 1) {
    const row = index % rows;
    const column = index % columns;
    members.push({
      id: `member-${index}`,
      case_revision_id: `case-${String(row).padStart(3, '0')}`,
      variant_hash: `variant-${column}`,
      params: { column },
      required: column % 2 === 0,
      ordinal: index,
    });
  }
  return { ...revision, members, edges: [] };
}

/**
 * The deferred resolver by hand: `Promise.withResolvers` is ES2024 and this project compiles
 * against the ES2022 lib, so the executor is how the resolve handle escapes. The executor runs
 * synchronously, so the handle is real by the time a test settles the load.
 */
function deferred<T>(): { readonly promise: Promise<T>; readonly resolve: (value: T) => void } {
  let resolve: (value: T) => void = () => undefined;
  const promise = new Promise<T>((settle) => {
    resolve = settle;
  });
  return { promise, resolve };
}

describe('SuiteCompositionComponent', () => {
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

  it('renders fixture members at their case revision and variant intersections', async () => {
    const { root } = await open();
    expect(getRevision).toHaveBeenCalledExactlyOnceWith(revision.id);
    const table = root.querySelector('table[data-testid="suite-matrix"]');
    expect(table?.querySelector('caption')?.textContent).toContain('Case revisions by variant');
    const columns = [...(table?.querySelectorAll('thead th[scope="col"] bdi.identifier') ?? [])];
    const rows = [...(table?.querySelectorAll('tbody tr') ?? [])];
    expect(columns).toHaveLength(3);
    expect(rows).toHaveLength(3);
    for (const member of revision.members) {
      const row = rows.find(
        (candidate) =>
          candidate.querySelector('th[scope="row"] bdi')?.textContent === member.case_revision_id,
      );
      const column = columns.findIndex(
        (candidate) => candidate.textContent === member.variant_hash,
      );
      expect(column).toBeGreaterThanOrEqual(0);
      const cell = row?.querySelectorAll('td').item(column);
      expect(cell?.querySelector('[data-suite-member-id]')?.textContent).toBe(member.id);
      expect(cell?.textContent).toContain(member.required ? 'Required' : 'Optional');
      expect(cell?.textContent).toContain(`Ordinal ${member.ordinal}`);
    }
    expect(table?.textContent).toContain('browser=chromium');
    expect(table?.textContent).toContain('locale=vi');
    expect(table?.textContent).toContain('No parameters');
    const firstRow = rows.at(0);
    expect(firstRow?.querySelectorAll('td').item(1)?.textContent?.trim()).toBe('No member');
  });

  it('renders the revision identity, strategy and dependency direction from the fixture', async () => {
    const { root } = await open();
    expect(root.querySelector('[data-revision-no]')?.textContent).toBe(
      String(revision.revision_no),
    );
    expect(root.querySelector('[data-revision-status] bdi')?.textContent).toBe(revision.status);
    expect(root.querySelector('header .identifier')?.textContent).toBe(revision.revision_hash);
    const strategy = root.querySelector('[data-testid="suite-strategy"]');
    expect(strategy?.textContent).toContain('Shard count');
    expect(strategy?.querySelector('dd')?.textContent).toBe('2');
    expect(strategy?.textContent).toContain('Duration');
    const dependencyIds = [...root.querySelectorAll('.dependencies dd bdi')].map(
      (node) => node.textContent,
    );
    expect(dependencyIds).toEqual(
      revision.edges.flatMap((edge) => [edge.parent_member_id, edge.child_member_id]),
    );
  });

  it.each([
    ['draft', revision],
    ['published', publishedRevision],
  ] as const)('keeps the %s revision and strategy entirely read-only', async (status, body) => {
    getRevision.mockResolvedValue(body);
    const { root } = await open(body.id);
    expect(root.querySelector(editAffordances)).toBeNull();
    expect(root.querySelector('a')).toBeNull();
    expect(root.querySelector('[data-testid="suite-strategy"]')?.textContent).toContain(
      'read-only',
    );
    const notice = root.querySelector('[data-testid="suite-published-notice"]');
    if (status === 'published') {
      expect(notice?.textContent).toContain('immutable and read-only');
      expect(root.querySelector('[data-testid="suite-strategy"]')?.textContent).toContain(
        'No sharding',
      );
    } else {
      expect(notice).toBeNull();
      expect(root.textContent).toContain('This draft is shown read-only');
    }
  });

  it('renders the real warning component as unavailable instead of implying zero warnings', async () => {
    const { root } = await open();
    const warnings = root.querySelector('app-suite-warning-list');
    expect(warnings).not.toBeNull();
    expect(warnings?.querySelector('[data-testid="suite-warnings-unavailable"]')).not.toBeNull();
    expect(warnings?.textContent).not.toContain('No warnings');
  });

  it('keeps its constant heading and shows the empty message inside the matrix section', async () => {
    getRevision.mockResolvedValue(emptyRevision);
    const { root } = await open(emptyRevision.id);
    const heading = root.querySelector('[data-testid="suite-composition-state"]');
    expect(heading?.textContent).toBe('Suite composition');
    expect(document.activeElement).toBe(heading);
    const emptyMessage = root.querySelector('[data-testid="suite-matrix-empty"]');
    expect(emptyMessage?.textContent).toBe('No members in this revision');
    expect(root.textContent).toContain('no composition matrix to display');
    expect(root.textContent).toContain('No dependencies declared');
    expect(root.querySelector('[data-testid="suite-matrix"]')).toBeNull();
    expect(root.getAttribute('data-suite-composition-status')).toBe('ready');
    expect(root.querySelector('[data-testid="suite-strategy"]')?.textContent).toContain(
      'No sharding',
    );
  });

  it('draws the matrix for a small grid and no member table at all', async () => {
    getRevision.mockResolvedValue(gridRevision(3, 3));
    const { root } = await open();

    expect(root.querySelector('[data-testid="suite-matrix"]')).not.toBeNull();
    expect(root.querySelectorAll('[data-suite-matrix-row]')).toHaveLength(3);
    expect(root.querySelector('[data-testid="suite-members-flat"]')).toBeNull();
    expect(root.querySelector('[data-testid="suite-flat-note"]')).toBeNull();
    expect(root.querySelector('[data-testid="suite-members-more"]')).toBeNull();
  });

  it('lists an oversized grid flat in chunks of 200 and keeps focus on Show more', async () => {
    getRevision.mockResolvedValue(gridRevision(60, 60));
    const { root, harness } = await open();
    const flat = root.querySelector('[data-testid="suite-members-flat"]');

    expect(root.querySelector('[data-testid="suite-matrix"]')).toBeNull();
    expect(root.querySelector('[data-testid="suite-flat-note"]')?.textContent).toContain(
      'flat table',
    );
    expect(flat?.querySelectorAll('thead th')).toHaveLength(5);
    expect(flat?.querySelectorAll('tbody tr')).toHaveLength(200);
    const shown = root.querySelector('[data-testid="suite-members-shown"]');
    expect(shown?.getAttribute('role')).toBe('status');
    expect(shown?.textContent).toContain('Showing 200 of 3600 members');
    const first = flat?.querySelector('tbody tr');
    expect(first?.textContent).toContain('case-000');
    expect(first?.textContent).toContain('column=0');
    expect(first?.textContent).toContain('Required');
    expect(first?.querySelector('[data-suite-flat-member-id]')?.textContent).toBe('member-0-0');

    const more = root.querySelector<HTMLButtonElement>('[data-testid="suite-members-more"]');
    expect(more?.tagName).toBe('BUTTON');
    more?.focus();
    more?.click();
    harness.fixture.detectChanges();

    expect(flat?.querySelectorAll('tbody tr')).toHaveLength(400);
    expect(shown?.textContent).toContain('Showing 400 of 3600 members');
    expect(document.activeElement).toBe(more);
  });

  it('chunks a long dependency list and tracks each row by both member ids', async () => {
    const edges = Array.from({ length: 201 }, (_, index) => ({
      parent_member_id: `parent-${index}`,
      child_member_id: `child-${index}`,
    }));
    getRevision.mockResolvedValue({ ...revision, edges });
    const { root, harness } = await open();
    const rows = () => root.querySelectorAll('.dependencies li');

    expect(rows()).toHaveLength(200);
    const shown = root.querySelector('[data-testid="suite-dependencies-shown"]');
    expect(shown?.getAttribute('role')).toBe('status');
    expect(shown?.textContent).toContain('Showing 200 of 201 dependencies');

    const more = root.querySelector<HTMLButtonElement>('[data-testid="suite-dependencies-more"]');
    more?.focus();
    more?.click();
    harness.fixture.detectChanges();

    expect(rows()).toHaveLength(201);
    // The control outlives its last chunk, so the focus a keyboard user spent on it survives.
    expect(more?.isConnected).toBe(true);
    expect(more?.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(more);
    expect(shown?.textContent).toContain('Showing 201 of 201 dependencies');
    const firstRow = rows().item(0);
    expect(
      [...(firstRow?.querySelectorAll('dd bdi') ?? [])].map((node) => node.textContent),
    ).toEqual(['parent-0', 'child-0']);
    const lastRow = rows().item(200);
    expect(
      [...(lastRow?.querySelectorAll('dd bdi') ?? [])].map((node) => node.textContent),
    ).toEqual(['parent-200', 'child-200']);
  });

  it('lists every member of an oversized sparse grid that fits one chunk, with no control', async () => {
    getRevision.mockResolvedValue(sparseGridRevision(61, 41, 200));
    const { root } = await open();
    const flat = root.querySelector('[data-testid="suite-members-flat"]');

    // The grid is too large for a matrix, yet the member list is not chunked: the table must still
    // render every member, because the layout decision is about cells, not about the member count.
    expect(root.querySelector('[data-testid="suite-flat-note"]')).not.toBeNull();
    expect(flat?.querySelectorAll('tbody tr')).toHaveLength(200);
    expect(root.querySelector('[data-testid="suite-members-shown"]')).toBeNull();
    expect(root.querySelector('[data-testid="suite-members-more"]')).toBeNull();
  });

  it('restarts both chunk windows when another oversized revision opens', async () => {
    const next = {
      ...gridRevision(60, 60),
      id: '018f0000-0000-7000-8000-00000000c901',
      edges: Array.from({ length: 201 }, (_, index) => ({
        parent_member_id: `p-${index}`,
        child_member_id: `c-${index}`,
      })),
    };
    getRevision.mockResolvedValue(gridRevision(60, 60));
    const { harness, root } = await open();
    const memberRows = () => root.querySelectorAll('[data-testid="suite-members-flat"] tbody tr');
    const dependencyRows = () => root.querySelectorAll('.dependencies li');
    expect(memberRows()).toHaveLength(200);

    root.querySelector<HTMLButtonElement>('[data-testid="suite-members-more"]')?.click();
    harness.fixture.detectChanges();
    expect(memberRows()).toHaveLength(400);

    getRevision.mockResolvedValue(next);
    await harness.navigateByUrl(
      `/projects/project-1/suites/revisions/${next.id}`,
      SuiteCompositionComponent,
    );
    await harness.fixture.whenStable();
    harness.fixture.detectChanges();

    // A new revision starts at one chunk again instead of inheriting how far the last one was
    // opened; the dependency list, chunked for the first time here, does the same.
    expect(memberRows()).toHaveLength(200);
    expect(dependencyRows()).toHaveLength(200);
  });

  it('keeps the exhausted member control in place and makes its last press a no-op', async () => {
    getRevision.mockResolvedValue(sparseGridRevision(61, 41, 201));
    const { root, harness } = await open();
    const rows = () => root.querySelectorAll('[data-testid="suite-members-flat"] tbody tr');

    expect(rows()).toHaveLength(200);
    const shown = root.querySelector('[data-testid="suite-members-shown"]');
    expect(shown?.textContent).toContain('Showing 200 of 201 members');
    const more = root.querySelector<HTMLButtonElement>('[data-testid="suite-members-more"]');
    expect(more?.getAttribute('aria-disabled')).toBe('false');
    more?.focus();
    more?.click();
    harness.fixture.detectChanges();

    expect(rows()).toHaveLength(201);
    expect(shown?.textContent).toContain('Showing 201 of 201 members');
    expect(more?.isConnected).toBe(true);
    expect(more?.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(more);

    more?.click();
    harness.fixture.detectChanges();
    expect(rows()).toHaveLength(201);
    expect(document.activeElement).toBe(more);
  });

  it('is activated by Enter and Space, which the page never captures', async () => {
    getRevision.mockResolvedValue(sparseGridRevision(61, 41, 201));
    const { root, harness } = await open();
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

    // The click a keyboard activation produces carries no pointer detail; that event, not the key,
    // is what the control acts on, and focus stays on it afterwards.
    more.dispatchEvent(new MouseEvent('click', { bubbles: true, detail: 0 }));
    harness.fixture.detectChanges();
    expect(rows()).toHaveLength(201);
    expect(document.activeElement).toBe(more);
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

  it('reports an address naming no revision as not found instead of loading forever', async () => {
    const fixture = TestBed.createComponent(SuiteCompositionComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.getAttribute('data-suite-composition-status')).toBe('not-found');
    expect(root.querySelector('[data-testid="suite-composition"]')?.getAttribute('aria-busy')).toBe(
      'false',
    );
    const heading = root.querySelector('[data-testid="suite-composition-state"]');
    expect(heading?.textContent).toBe('Suite revision not found');
    expect(document.activeElement).toBe(heading);
    expect(getRevision).not.toHaveBeenCalled();
  });

  it('explains an unreadable strategy rather than displaying a guessed shard count', async () => {
    getRevision.mockResolvedValue({ ...revision, shard_strategy: { kind: 'future' } });
    const { root } = await open();
    const strategy = root.querySelector('[data-testid="suite-strategy"]');
    expect(strategy?.textContent).toContain('Strategy unavailable');
    expect(strategy?.querySelector('dd')).toBeNull();
    expect(root.querySelector('[data-testid="suite-matrix"]')).not.toBeNull();
  });

  it.each([
    ['not-found', notFound, 'Suite revision not found'],
    ['forbidden', forbidden, 'Suite revision access denied'],
    ['unavailable', unavailable, 'Suite composition unavailable'],
  ] as const)(
    'renders and focuses the %s state from its fixture failure',
    async (status, input, title) => {
      getRevision.mockRejectedValue(
        new ApiRequestError(
          mapApiError({
            status: input.status,
            body: input.body,
            headers: new Headers(input.headers),
          }),
        ),
      );
      const { root } = await open('inaccessible-revision');
      const heading = root.querySelector('[data-testid="suite-composition-state"]');
      expect(root.getAttribute('data-suite-composition-status')).toBe(status);
      expect(heading?.textContent).toBe(title);
      expect(document.activeElement).toBe(heading);
      expect(root.querySelector('[data-testid="suite-matrix"]')).toBeNull();
      expect(root.textContent).not.toContain('inaccessible-revision');
      expect(root.querySelector(editAffordances)).toBeNull();
    },
  );

  it('renders and focuses an unclassified failure without exposing exception text', async () => {
    getRevision.mockRejectedValue(new Error('<script>private diagnostic</script>'));
    const { root } = await open();
    const heading = root.querySelector('[data-testid="suite-composition-state"]');
    expect(root.getAttribute('data-suite-composition-status')).toBe('error');
    expect(heading?.textContent).toBe('Suite composition could not be loaded');
    expect(document.activeElement).toBe(heading);
    expect(root.textContent).not.toContain('private diagnostic');
    expect(root.querySelector('script')).toBeNull();
  });

  it('moves focus from the loading heading to the committed ready heading', async () => {
    const pending = deferred<SuiteRevisionDto>();
    getRevision.mockReturnValue(pending.promise);
    const { harness, root } = await open();
    const heading = root.querySelector('[data-testid="suite-composition-state"]');
    expect(root.getAttribute('data-suite-composition-status')).toBe('loading');
    expect(root.querySelector('[data-testid="suite-composition"]')?.getAttribute('aria-busy')).toBe(
      'true',
    );
    expect(heading?.textContent).toBe('Loading suite composition');
    expect(document.activeElement).toBe(heading);
    root.tabIndex = -1;
    root.focus();
    expect(document.activeElement).toBe(root);
    pending.resolve(revision);
    await harness.fixture.whenStable();
    // Zoneless: a settled promise is not a pending task, so `whenStable()` can resolve with the
    // store at `ready` and the view still rendered from `loading`. The pass below renders the
    // committed status, which is what the heading and its focus move belong to.
    harness.fixture.detectChanges();
    expect(root.getAttribute('data-suite-composition-status')).toBe('ready');
    expect(root.querySelector('[data-testid="suite-composition"]')?.getAttribute('aria-busy')).toBe(
      'false',
    );
    expect(heading?.textContent).toBe('Suite composition');
    expect(document.activeElement).toBe(heading);
  });

  it('loads a new revision when the router reuses the component', async () => {
    const { harness, root } = await open();
    getRevision.mockResolvedValue(publishedRevision);
    await harness.navigateByUrl(
      `/projects/project-1/suites/revisions/${publishedRevision.id}`,
      SuiteCompositionComponent,
    );
    await harness.fixture.whenStable();
    expect(getRevision).toHaveBeenLastCalledWith(publishedRevision.id);
    expect(root.querySelector('[data-revision-no]')?.textContent).toBe(
      String(publishedRevision.revision_no),
    );
    expect(root.querySelector('[data-testid="suite-published-notice"]')).not.toBeNull();
  });

  it('renders member parameters as isolated text, never server-provided markup', async () => {
    const members = revision.members.map((member) => ({
      ...member,
      params: { browser: '<img src=x onerror=alert(1)>' },
    }));
    getRevision.mockResolvedValue({ ...revision, members });
    const { root } = await open();
    expect(root.querySelector('.parameters bdi')?.textContent).toBe(
      'browser=<img src=x onerror=alert(1)>',
    );
    expect(root.querySelector('img')).toBeNull();
  });
});
