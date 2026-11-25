import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { formatDate, registerLocaleData } from '@angular/common';
import viLocale from '@angular/common/locales/vi';
import { LOCALE_ID, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { By } from '@angular/platform-browser';
import { DataTableComponent } from '../../../shared/ui/data-table/data-table.component';
import { useLightweightTablePresentation } from '../../../shared/ui/data-table/data-table-volume.testing';
import { CopyValueButtonHarness } from '../../../shared/ui/values/values.harness';
import type { RunSummary } from '@evidgate/sdk';
import { RunListTableComponent } from './run-list-table.component';

function run(index: number, changes: Partial<RunSummary> = {}): RunSummary {
  return {
    run_id: `018f0000-0000-7000-8000-${String(index).padStart(12, '0')}`,
    state: 'complete',
    created_at: '2026-10-27T00:00:00Z',
    ref: {
      kind: 'branch',
      name: 'main',
      pr_number: null,
      head_sha: null,
      base_sha: null,
      trigger_event: 'push',
      is_fork: false,
    },
    unverified_ref: false,
    decision: null,
    completeness: 'complete',
    parent_run_id: null,
    rerun_scope: null,
    ...changes,
  };
}

class RunInspectionHarness extends ComponentHarness {
  static hostSelector = 'app-run-list-table';
  readonly view = this.locatorFor('button[aria-haspopup="dialog"]');
  readonly viewIcon = this.locatorFor('button[aria-haspopup="dialog"] hlm-icon');
  readonly rerunLabel = this.locatorFor('td [data-run-rerun] app-data-table-value');
  readonly rerunText = this.locatorFor('td [data-run-rerun] app-data-table-value .value');
  viewFor(id: string) {
    return this.locatorFor(`button[aria-label="View ${id}"]`)();
  }
  readonly fields = this.documentRootLocatorFactory().locatorForAll(
    'dl.hlm-field-grid > div > dt + dd',
  );
  readonly dialog = this.documentRootLocatorFactory().locatorFor('[role="dialog"]');
  readonly openDialog = this.documentRootLocatorFactory().locatorForOptional('[role="dialog"]');
  readonly close = this.documentRootLocatorFactory().locatorFor('[role="dialog"] [hlmDialogClose]');
}

// Volume cases still render real 200-row tables; retain their existing DOM-test budget.
describe('RunListTableComponent', { timeout: 15_000 }, () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [RunListTableComponent],
      providers: [provideZonelessChangeDetection(), provideRouter([])],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());
  async function render(rows: readonly RunSummary[]) {
    const fixture = TestBed.createComponent(RunListTableComponent);
    fixture.componentRef.setInput('rows', rows);
    fixture.componentRef.setInput('projectId', 'project-a');
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }
  it('uses Helm tables and badges while retaining a single accessible table', async () => {
    const { root } = await render([run(1, { decision: 'PASS', rerun_label: 'failed_subset' })]);
    expect(root.querySelectorAll('[role="table"]')).toHaveLength(1);
    expect(root.querySelectorAll('table[data-slot="table"][role="presentation"]')).toHaveLength(1);
    expect(root.querySelector('[data-run-row] a')).not.toBeNull();
    expect(root.querySelector('[data-run-decision] [hlmBadge]')).not.toBeNull();
    expect(root.querySelector('[data-run-rerun] [hlmBadge]')).toBeNull();
    expect(root.querySelector('.eg-table-layout')).not.toBeNull();
  });
  it('budgets six desktop columns with a flexible human-readable subject', async () => {
    const { fixture, root } = await render([run(1)]);
    const table = fixture.debugElement
      .query(By.directive(DataTableComponent))
      .injector.get(DataTableComponent);
    expect(table.fitToContainer()).toBe(true);
    const minimums = {
      id: 'calc(16ch + var(--eg-space-4) * 2)',
      state: 'calc(12ch + var(--eg-space-4) * 2)',
      evidence: 'calc(14ch + var(--eg-space-4) * 2)',
      decision: 'calc(15ch + var(--eg-space-4) * 2)',
      scope: 'calc(13ch + var(--eg-space-4) * 2)',
      actions: 'max-content',
    };
    for (const [id, minimum] of Object.entries(minimums)) {
      expect(table.columns().find((column) => column.id === id)?.width?.min).toBe(minimum);
      const cells = root.querySelectorAll<HTMLElement>(`th.cdk-column-${id},td.cdk-column-${id}`);
      expect(cells).toHaveLength(2);
      for (const cell of cells) {
        expect(cell.style.getPropertyValue('min-inline-size')).toBe(minimum);
      }
    }
    expect(root.querySelector<HTMLElement>('th.cdk-column-id')?.style.inlineSize).toBe('auto');
    expect(root.querySelector<HTMLElement>('th.cdk-column-scope')?.style.inlineSize).toBe(
      'max-content',
    );
    expect(root.querySelectorAll('[role="columnheader"]')).toHaveLength(6);
    expect(
      root.querySelector('[data-run-row] [data-run-created] time')?.getAttribute('datetime'),
    ).toBe(run(1).created_at);
    const identity = root.querySelector('[data-run-row] a');
    expect(identity?.querySelector('bdi')?.textContent?.trim()).toBe('main');
    expect(identity?.textContent).toContain(run(1).run_id);
  });
  it('keeps the empty table verdict-free with only the header row counted', async () => {
    const { root } = await render([]);
    expect(root.querySelector('[role="table"]')?.getAttribute('aria-rowcount')).toBe('1');
    expect(root.querySelector('[data-run-row]')).toBeNull();
    expect(root.querySelector('app-decision-chip')).toBeNull();
  });
  it('links the row id to its detail child and preserves the current filter query', async () => {
    await TestBed.inject(Router).navigateByUrl('/?state=complete&ref=main');
    const { root } = await render([run(1)]);
    const link = root.querySelector<HTMLAnchorElement>('.identifier a')!;
    expect(link.getAttribute('href')).toBe(`/${run(1).run_id}?state=complete&ref=main`);
    expect(root.querySelectorAll('a[href]')).toHaveLength(1);
    expect(link.querySelector('bdi')?.textContent?.trim()).toBe('main');
    expect(link.getAttribute('aria-label')).toBeNull();
    // The link name comes from its content, so it contains every visible label (WCAG 2.5.3).
    expect(link.hasAttribute('aria-labelledby')).toBe(false);
    expect(link.textContent).toContain(`(${run(1).run_id})`);
    expect(link.querySelector('time')?.getAttribute('datetime')).toBe(run(1).created_at);
    expect(link.querySelector('bdi')?.classList.contains('truncate')).toBe(true);
    expect(link.closest('.identifier')?.classList.contains('min-w-0')).toBe(true);
  });
  it('opens the selected run in place and returns focus to View without changing the filter', async () => {
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/?state=complete&pageSize=50&cursor=opaque-page');
    const { fixture } = await render([run(3, { decision: 'FAIL' })]);
    const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, RunInspectionHarness);
    const view = await table.viewFor(run(3).run_id);
    expect(await view.getAttribute('aria-label')).toBe(`View ${run(3).run_id}`);
    expect(await view.text({ exclude: '[popover]' })).toBe('');
    expect(await (await table.viewIcon()).getAttribute('aria-hidden')).toBe('true');
    await view.focus();
    await view.click();
    const dialog = await table.dialog();
    expect(await dialog.text()).toContain(run(3).run_id);
    expect(await dialog.text()).toContain('FAIL');
    expect(await table.fields()).toHaveLength(7);
    expect(router.url).toBe('/?state=complete&pageSize=50&cursor=opaque-page');
    await (await table.close()).click();
    expect(await view.isFocused()).toBe(true);
    expect(router.url).toBe('/?state=complete&pageSize=50&cursor=opaque-page');
  });
  it('returns focus to the replacement View button after the table becomes narrow', async () => {
    const { fixture } = await render([run(1), run(3)]);
    const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, RunInspectionHarness);
    const opener = await table.viewFor(run(3).run_id);
    await opener.focus();
    await opener.click();
    fixture.debugElement
      .query(By.directive(DataTableComponent))
      .injector.get(DataTableComponent)
      .narrow.set(true);
    await fixture.whenStable();
    await (await table.close()).click();
    const replacement = await table.viewFor(run(3).run_id);
    expect(await replacement.isFocused()).toBe(true);
  });
  it('keeps the selected run current on append and closes it permanently when removed', async () => {
    const { fixture } = await render([run(3)]);
    const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, RunInspectionHarness);
    await (await table.view()).click();
    fixture.componentRef.setInput('rows', [run(3, { decision: 'FAIL' }), run(4)]);
    expect(await (await table.dialog()).text()).toContain('FAIL');
    fixture.componentRef.setInput('rows', [run(4)]);
    expect(await table.openDialog()).toBeNull();
    fixture.componentRef.setInput('rows', [run(3), run(4)]);
    expect(await table.openDialog()).toBeNull();
  });
  it('closes inspection on project change even if the same run ID is present', async () => {
    const { fixture } = await render([run(3)]);
    const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, RunInspectionHarness);
    await (await table.view()).click();
    fixture.componentRef.setInput('projectId', 'project-b');
    expect(await table.openDialog()).toBeNull();
  });
  it.each([20, 50, 100])('renders a %i-row server page without a nested pager', async (count) => {
    const { fixture, root } = await render(Array.from({ length: count }, (_, index) => run(index)));
    const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, RunInspectionHarness);
    expect(await table.viewFor(run(count - 1).run_id)).toBeDefined();
    expect(root.querySelectorAll('[data-run-row]')).toHaveLength(count);
    expect(root.querySelector('app-data-table-pager')).toBeNull();
    expect(root.querySelector('[data-table-pagination]')).toBeNull();
  });
  it('keeps stored evidence independent from lifecycle and release decisions', async () => {
    const unavailable = run(5);
    delete unavailable.decision;
    const { root, fixture } = await render([
      run(1, { decision: 'PASS' }),
      run(2, { state: 'incomplete', completeness: 'complete', decision: 'FAIL' }),
      run(3, { decision: 'INCONCLUSIVE' }),
      run(4, { completeness: null, decision: null }),
      unavailable,
    ]);
    expect(
      [...root.querySelectorAll('[role="columnheader"]')].map((cell) => cell.textContent?.trim()),
    ).toEqual(['Run (oldest first)', 'Decision', 'State', 'Evidence', 'Run scope', 'Actions']);
    expect(root.querySelector('th.cdk-column-id button')).toBeNull();
    expect(
      [...root.querySelectorAll('[data-run-evidence] [role="img"]')].map((badge) =>
        badge.getAttribute('aria-label'),
      ),
    ).toEqual([
      'Completeness: Complete',
      'Completeness: Complete',
      'Completeness: Complete',
      'Completeness: Unavailable',
      'Completeness: Complete',
    ]);
    expect(root.querySelectorAll('[data-run-evidence-hint]')).toHaveLength(1);
    expect(root.querySelector('[data-run-evidence-hint]')?.textContent).toContain(
      'Open the run for evidence completeness.',
    );
    expect(
      [...root.querySelectorAll('[data-run-decision]')].map((cell) => ({
        dimension: cell.querySelector('.verdict__label')?.textContent?.trim(),
        value: cell.querySelector('.verdict__value')?.textContent?.trim(),
      })),
    ).toEqual([
      { dimension: 'Decision:', value: 'PASS' },
      { dimension: 'Decision:', value: 'FAIL' },
      { dimension: 'Decision:', value: 'INCONCLUSIVE' },
      { dimension: 'Decision:', value: 'Pending' },
      { dimension: 'Decision:', value: 'Unavailable' },
    ]);
    expect(root.querySelector('app-outcome-pill')).toBeNull();
    expect(root.querySelector('time')?.getAttribute('datetime')).toBe('2026-10-27T00:00:00Z');
    expect(root.querySelector('[data-run-row] a')?.textContent).toContain(run(1).run_id);
    expect(root.querySelectorAll('[data-run-decision] [data-tone="pending"]')).toHaveLength(1);
    expect(root.querySelectorAll('[data-run-decision] [data-tone="neutral"]')).toHaveLength(1);
    const reason = 'The decision could not be read. Try again.';
    const cell = root.querySelector(`[data-row-id="${unavailable.run_id}"] [data-run-decision]`)!;
    expect(cell.querySelector('.verdict__detail')).toBeNull();
    expect(cell.querySelector('.sr-only:not(.verdict__label)')?.textContent?.trim()).toBe(reason);
    expect(root.querySelector('[data-run-decision] [title]')).toBeNull();
    const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, RunInspectionHarness);
    await (await table.viewFor(unavailable.run_id)).click();
    expect(await (await table.dialog()).text({ exclude: '.sr-only' })).toContain(reason);
  });
  it('keeps dense state and evidence labels short with the explanation outside the badge', async () => {
    const { root } = await render([run(1, { completeness: 'incomplete' })]);
    expect(root.querySelector('app-run-state-pill')?.textContent?.trim()).toBe('Complete');
    const evidence = root.querySelector('[data-run-evidence]');
    expect(evidence?.querySelector('[role="img"]')?.textContent?.trim()).toBe('Incomplete');
    expect(root.querySelector('[data-run-evidence-hint]')).toBeNull();
    expect(evidence?.textContent).not.toContain('Open the run for evidence completeness.');
    expect(evidence?.querySelector('[role="img"]')?.textContent).not.toContain('Open the run');
  });
  it('uses the shared UTC absolute-time format without losing the server instant', async () => {
    const instant = '2026-11-02T18:47:05.130948+07:00';
    const { root } = await render([run(1, { created_at: instant })]);
    const time = root.querySelector('time');
    expect(time?.getAttribute('datetime')).toBe(instant);
    expect(time?.textContent?.replace(/\s+/g, ' ').trim()).toBe('11/2/2026, 11:47:05 AM UTC');
  });
  it.each([['not-a-date'], ['']])(
    'refuses to render an unusable server time %j instead of failing the row',
    async (createdAt) => {
      const { root } = await render([run(1, { created_at: createdAt })]);
      expect(root.querySelector('time')).toBeNull();
      const cell = root.querySelector('[data-run-row] app-absolute-time');
      expect(cell?.textContent?.trim()).toBe('Time unavailable');
    },
  );
  it('formats the same UTC instant with Vietnamese date and time conventions', async () => {
    registerLocaleData(viLocale);
    TestBed.overrideProvider(LOCALE_ID, { useValue: 'vi' });
    const instant = '2026-11-02T18:47:05.130948+07:00';
    const { root } = await render([run(1, { created_at: instant })]);
    expect(root.querySelector('time')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      `${formatDate(instant, 'HH:mm:ss dd/MM/yyyy', 'vi', 'UTC')} UTC`,
    );
    expect(root.querySelector('time')?.getAttribute('datetime')).toBe(instant);
  });
  it('uses primary/secondary column roles and renders six facts as shared narrow records', async () => {
    const { fixture, root } = await render([
      run(1, {
        rerun_label: 'retry-'.repeat(80),
        reprocess_label: 'evidence-'.repeat(80),
      }),
    ]);
    const headers = [...root.querySelectorAll('[role="columnheader"]')];
    expect(headers.map((cell) => cell.getAttribute('data-column-role'))).toEqual([
      'identity',
      'status',
      'status',
      'status',
      'text',
      'actions',
    ]);
    expect(headers[0]?.getAttribute('data-column-priority')).toBe('primary');
    expect(headers[0]?.getAttribute('data-column-kind')).toBe('branch');
    fixture.debugElement
      .query(By.directive(DataTableComponent))
      .injector.get(DataTableComponent)
      .narrow.set(true);
    await fixture.whenStable();
    const record = root.querySelector('.eg-table-record');
    expect(record?.querySelectorAll('dt')).toHaveLength(6);
    expect(record?.querySelectorAll('dd')).toHaveLength(6);
    expect(record?.querySelector('[data-run-created] time')?.getAttribute('datetime')).toBe(
      run(1).created_at,
    );
    expect(record?.querySelector('a bdi')?.textContent).toBe('main');
    expect(record?.querySelector('a')?.getAttribute('href')).toBe(`/${run(1).run_id}`);
    expect(record?.querySelector('a')?.classList.contains('whitespace-nowrap')).toBe(false);
    expect(record?.querySelector('.identifier')?.classList.contains('min-w-max')).toBe(false);
    expect(record?.querySelector('.identifier')?.classList.contains('min-w-0')).toBe(true);
    expect(record?.textContent).toContain('retry-'.repeat(80));
    expect(record?.textContent).toContain('evidence-'.repeat(80));
    expect(root.querySelector('.eg-table-narrow')).not.toBeNull();
    expect(root.querySelector('[role="table"]')).toBeNull();
  });
  it('escapes every Cf code point, isolates Unicode refs and only marks unverified rows', async () => {
    const special = 'release/việt\u202e\u200d<script>';
    const row = run(1);
    const { root } = await render([
      run(1, { ref: { ...row.ref, name: special }, unverified_ref: true }),
      run(2),
    ]);
    const ref = root.querySelector('[data-run-row] a bdi')!;
    expect(ref.textContent).toContain('release/việt⟨U+202E⟩⟨U+200D⟩<script>');
    expect(ref.textContent).not.toContain('\u202e');
    expect(root.querySelector('script')).toBeNull();
    expect(root.querySelectorAll('[data-run-unverified]')).toHaveLength(1);
    expect(
      ref.parentElement?.querySelector('[data-run-unverified]')?.getAttribute('aria-label'),
    ).toBe('Unverified ref');
    expect(
      root
        .querySelector('[data-run-unverified]')
        ?.closest('tr[data-row-id]')
        ?.getAttribute('data-row-id'),
    ).toBe(row.run_id);
  });
  it('uses an absolute UTC subject instead of an absent ref and keeps real refs monospaced', async () => {
    const row = run(1);
    const { root } = await render([run(1), run(2, { ref: { ...row.ref, name: null } })]);
    const subjects = root.querySelectorAll('[data-run-row] a');
    expect(subjects[0]?.querySelector('bdi.font-mono')?.textContent).toBe('main');
    expect(subjects[1]?.querySelector('time')?.getAttribute('datetime')).toBe(row.created_at);
    expect(subjects[1]?.textContent).toContain('UTC');
    expect(subjects[1]?.querySelector('.font-mono')).toBeNull();
  });
  it('escapes and isolates format characters in rerun and reprocess labels', async () => {
    const { root } = await render([
      run(1, { rerun_label: 'failed\u202E_subset', parent_run_id: run(9).run_id }),
      run(2, { reprocess_label: 'evidence\u202E:reprocess', parent_run_id: run(9).run_id }),
    ]);
    const rerun = root.querySelector('[data-run-rerun]')!;
    const reprocess = root.querySelector('[data-run-reprocess]')!;
    // The label is escaped like the ref name: the override becomes a visible token, never a raw
    // character that could reorder the rest of the row.
    expect(rerun.textContent).toContain('failed⟨U+202E⟩_subset');
    expect(rerun.textContent).not.toContain('\u202E');
    expect(reprocess.textContent).toContain('evidence⟨U+202E⟩:reprocess');
    expect(reprocess.textContent).not.toContain('\u202E');
    // Both are wrapped in `<bdi>`, so even a label without a Cf character cannot reorder columns.
    expect(rerun.querySelector('bdi')).not.toBeNull();
    expect(reprocess.querySelector('bdi')).not.toBeNull();
    expect(rerun.querySelector('[title]')?.getAttribute('title')).toBe('failed⟨U+202E⟩_subset');
    expect(reprocess.querySelector('[title]')?.getAttribute('title')).toBe(
      'evidence⟨U+202E⟩:reprocess',
    );
  });
  it('renders rerun and reprocess as visibly distinct labels without treating either as full regression', async () => {
    const { fixture, root } = await render([
      run(1, { rerun_label: 'failed_subset', parent_run_id: run(9).run_id }),
      run(2, { reprocess_label: 'reprocessed', parent_run_id: run(9).run_id }),
      run(3),
      run(4, { parent_run_id: run(9).run_id }),
    ]);
    expect(root.querySelector('[data-run-rerun] strong')?.textContent).toBe('Rerun');
    const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, RunInspectionHarness);
    expect(await (await table.rerunLabel()).getAttribute('title')).toBe('failed_subset');
    const label = await table.rerunText();
    expect(await label.text()).toBe('failed_subset');
    expect(await label.hasClass('truncate')).toBe(false);
    expect(root.querySelector('[data-run-reprocess] strong')?.textContent).toBe('Reprocessed');
    expect(root.querySelector('[data-run-reprocess]')?.textContent).toContain('reprocessed');
    expect(root.querySelector('[data-run-rerun]')?.classList.contains('scope-rerun')).toBe(true);
    expect(root.querySelector('[data-run-reprocess]')?.classList.contains('scope-reprocess')).toBe(
      true,
    );
    expect(root.textContent).toContain('Original run');
    expect(root.textContent).toContain('Derived run');
  });
  it('retains the missing-ref explanation in View and the full long ref in the subject', async () => {
    const first = run(1);
    const name = 'feature/' + 'long-ref-'.repeat(60);
    const { fixture, root } = await render([
      run(1, { ref: { ...first.ref, name: null } }),
      run(2, { ref: { ...first.ref, name } }),
    ]);
    const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, RunInspectionHarness);
    await (await table.viewFor(first.run_id)).click();
    expect(await (await table.dialog()).text()).toContain('Ref unavailable');
    expect(root.textContent).toContain(name);
  });
  it.each([200, 201, 250])(
    'bounds %i rich rows without a fixed-height virtual probe or lost records',
    async (count) => {
      useLightweightTablePresentation();
      const input = Array.from({ length: count }, (_, index) =>
        run(index, {
          rerun_label: 'reason-'.repeat(80),
        }),
      );
      const { fixture, root } = await render(input);
      expect(root.querySelector('cdk-virtual-scroll-viewport')).toBeNull();
      expect(root.querySelector('.row-probe')).toBeNull();
      expect(root.querySelectorAll('[data-run-row]')).toHaveLength(200);
      expect(root.querySelector('[role="table"]')?.getAttribute('aria-rowcount')).toBe(
        String(count + 1),
      );
      const pager = root.querySelector('.eg-table-pagination');
      if (count === 200) {
        expect(pager).toBeNull();
      } else {
        const buttons = pager?.querySelectorAll<HTMLButtonElement>('button');
        expect(pager?.textContent).toContain(`1–200 of ${count}`);
        buttons?.[1]?.click();
        await fixture.whenStable();
        expect(root.querySelectorAll('[data-run-row]')).toHaveLength(count - 200);
        expect(root.querySelector('[data-run-row]')?.getAttribute('data-run-id')).toBe(
          run(200).run_id,
        );
        expect(pager?.textContent).toContain(`201–${count} of ${count}`);
        buttons?.[0]?.click();
        await fixture.whenStable();
        expect(root.querySelector('[data-run-row]')?.getAttribute('data-run-id')).toBe(
          run(0).run_id,
        );
      }
    },
  );
  it('copies the full id beside its row preview or from View without navigating', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    try {
      const { fixture, root } = await render([run(1)]);
      expect(root.querySelector('[data-run-id-preview]')?.textContent?.trim()).toBe('018f0…00001');
      const rowCopy =
        await TestbedHarnessEnvironment.loader(fixture).getHarness(CopyValueButtonHarness);
      expect(await rowCopy.actionName()).toBe('Copy Run id');
      await rowCopy.copy();
      expect(writeText).toHaveBeenCalledExactlyOnceWith(run(1).run_id);
      expect(await rowCopy.result()).toBe('Run id copied.');
      expect(root.querySelector('[role="dialog"]')).toBeNull();
      writeText.mockClear();
      const table = await TestbedHarnessEnvironment.harnessForFixture(
        fixture,
        RunInspectionHarness,
      );
      await (await table.viewFor(run(1).run_id)).click();
      const copy = document.querySelector<HTMLButtonElement>(
        '[role="dialog"] app-copy-value-button button',
      )!;
      expect(copy.getAttribute('aria-label')).toBe('Copy Run id');
      expect(copy.title).toBe(copy.getAttribute('aria-label'));
      expect(copy.querySelector('hlm-icon')).not.toBeNull();
      copy.click();
      await fixture.whenStable();
      expect(writeText).toHaveBeenCalledWith(run(1).run_id);
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
