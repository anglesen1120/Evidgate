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
  'button, input, select, textarea, [contenteditable], [role="button"], [role="textbox"], ' +
  '[role="combobox"], [role="slider"], [role="spinbutton"], [role="checkbox"], [role="switch"]';

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

  it('distinguishes an empty revision from an unavailable composition', async () => {
    getRevision.mockResolvedValue(emptyRevision);
    const { root } = await open(emptyRevision.id);
    expect(root.textContent).toContain('No members in this revision');
    const heading = root.querySelector('[data-testid="suite-composition-state"]');
    expect(heading?.textContent).toBe('No members in this revision');
    expect(document.activeElement).toBe(heading);
    expect(root.textContent).toContain('No dependencies declared');
    expect(root.querySelector('[data-testid="suite-matrix"]')).toBeNull();
    expect(root.getAttribute('data-suite-composition-status')).toBe('ready');
    expect(root.querySelector('[data-testid="suite-strategy"]')?.textContent).toContain(
      'No sharding',
    );
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
