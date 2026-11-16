import { BreakpointObserver, type BreakpointState } from '@angular/cdk/layout';
import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { BehaviorSubject } from 'rxjs';

import { DESKTOP_MUTATION_QUERY } from '../narrow/narrow-mutation-policy';

import { CopyableIdentifierComponent } from '../values/copyable-identifier.component';
import type { DataTableCellContext, DataTableColumn } from './data-table-column';
import { resolveTableColumns } from './data-table-column';
import { DataTableComponent } from './data-table.component';
import { DATA_TABLE_FIXTURE_COLUMNS, dataTableFixtureRows } from './data-table.fixture';
import type { DataTableFixtureRow } from './data-table.fixture';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ng-template #cell let-row>{{ row.note }}</ng-template>
    <ng-template #action let-row>
      <button type="button" data-test-mutation (click)="calls.push(row.id)">
        {{ actionLabel }}
      </button>
    </ng-template>
  `,
})
class CellTemplateHost {
  readonly template =
    viewChild.required<TemplateRef<DataTableCellContext<DataTableFixtureRow>>>('cell');
  readonly actionTemplate =
    viewChild.required<TemplateRef<DataTableCellContext<DataTableFixtureRow>>>('action');
  readonly actionLabel = 'Actions';
  readonly calls: string[] = [];
}

const columns = DATA_TABLE_FIXTURE_COLUMNS.map((column) => {
  const textColumn = { ...column };
  delete textColumn.kind;
  return textColumn;
});

describe('Shared table column layout and reflow', () => {
  let fixture: ComponentFixture<DataTableComponent<DataTableFixtureRow>>;
  let root: HTMLElement;

  let viewportWidth: number;
  let viewportStates: BehaviorSubject<BreakpointState>;
  beforeEach(async () => {
    viewportWidth = 1280;
    viewportStates = new BehaviorSubject<BreakpointState>({
      matches: true,
      breakpoints: { [DESKTOP_MUTATION_QUERY]: true },
    });
    await TestBed.configureTestingModule({
      imports: [DataTableComponent, CellTemplateHost],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: BreakpointObserver,
          useValue: {
            observe: (query: string) => {
              expect(query).toBe(DESKTOP_MUTATION_QUERY);
              return viewportStates.asObservable();
            },
            isMatched: (query: string) => query === DESKTOP_MUTATION_QUERY && viewportWidth >= 768,
          },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(DataTableComponent<DataTableFixtureRow>);
    root = fixture.nativeElement as HTMLElement;
    fixture.componentRef.setInput('columns', columns);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(2));
    fixture.componentRef.setInput('rowId', (row: DataTableFixtureRow) => row.id);
    fixture.componentRef.setInput('caption', 'Evidence / Bằng chứng');
    await fixture.whenStable();
  });

  function resizeViewport(width: number): void {
    viewportWidth = width;
    const matches = width >= 768;
    viewportStates.next({ matches, breakpoints: { [DESKTOP_MUTATION_QUERY]: matches } });
  }

  async function renderActions(width: number): Promise<ComponentFixture<CellTemplateHost>> {
    resizeViewport(width);
    vi.spyOn(root, 'clientWidth', 'get').mockReturnValue(Math.min(480, width));
    const host = TestBed.createComponent(CellTemplateHost);
    await host.whenStable();
    fixture.componentRef.setInput('columns', [
      columns[0]!,
      { ...columns[3]!, cellTemplate: host.componentInstance.actionTemplate() },
    ]);
    await fixture.whenStable();
    return host;
  }

  it('isolates sticky descendants below app chrome and disables native sticky offsets in page flow', async () => {
    expect(root.classList.contains('isolate')).toBe(true);
    expect(root.querySelector('tr.eg-table-sticky')).not.toBeNull();
    expect(root.style.getPropertyValue('--eg-table-header-height')).not.toBe('0px');
    fixture.componentRef.setInput('flow', 'page');
    await fixture.whenStable();
    expect(root.querySelector('tr.eg-table-sticky')).toBeNull();
    expect(root.querySelector('.eg-table-long')).toBeNull();
    expect(root.style.getPropertyValue('--eg-table-header-height')).toBe('0px');
    fixture.componentRef.setInput('flow', 'bounded');
    await fixture.whenStable();
    expect(root.querySelector('tr.eg-table-sticky')).not.toBeNull();
    expect(root.style.getPropertyValue('--eg-table-header-height')).not.toBe('0px');
  });

  it('retains the virtual header and its offset when page flow is requested', async () => {
    fixture.componentRef.setInput('rows', dataTableFixtureRows(201));
    fixture.componentRef.setInput('flow', 'page');
    await fixture.whenStable();
    expect(fixture.componentInstance.virtual()).toBe(true);
    expect(root.querySelector('tr.eg-table-sticky')).not.toBeNull();
    expect(root.style.getPropertyValue('--eg-table-header-height')).not.toBe('0px');
  });

  it('applies native width budgets independently of narrow-record priority and clears removed budgets', async () => {
    const budgeted: readonly DataTableColumn<DataTableFixtureRow>[] = [
      {
        id: 'code',
        header: 'Action',
        role: 'text',
        priority: 'primary',
        cell: (row) => row.note,
        width: { min: '8ch', preferred: '1%', max: '16ch' },
      },
      {
        id: 'hash',
        header: 'Object id',
        role: 'identity',
        priority: 'secondary',
        cell: (row) => row.hash,
        width: { min: '24ch', preferred: 'auto', max: '32ch' },
      },
    ];
    fixture.componentRef.setInput('columns', budgeted);
    await fixture.whenStable();
    for (const [role, expected] of [
      ['text', ['8ch', '1%', '16ch']],
      ['identity', ['24ch', 'auto', '32ch']],
    ] as const) {
      const cells = root.querySelectorAll<HTMLElement>(`[data-column-role="${role}"]`);
      expect(cells).toHaveLength(4); // col, header and both loaded rows
      for (const cell of cells) {
        expect([
          cell.style.getPropertyValue('min-inline-size'),
          cell.style.getPropertyValue('inline-size'),
          cell.style.getPropertyValue('max-inline-size'),
        ]).toEqual(expected);
      }
    }
    expect(
      root.querySelector('th[data-column-role="text"]')?.getAttribute('data-column-priority'),
    ).toBe('primary');
    expect(
      root.querySelector('th[data-column-role="identity"]')?.getAttribute('data-column-priority'),
    ).toBe('secondary');
    fixture.componentRef.setInput(
      'columns',
      budgeted.map((column) => {
        const next = { ...column };
        delete next.width;
        return next;
      }),
    );
    await fixture.whenStable();
    for (const cell of root.querySelectorAll<HTMLElement>('[data-column-role]')) {
      expect(cell.style.getPropertyValue('min-inline-size')).toBe('');
      expect(cell.style.getPropertyValue('inline-size')).toBe('');
      expect(cell.style.getPropertyValue('max-inline-size')).toBe('');
    }
  });

  it.each(['bounded', 'page'] as const)(
    'keeps 25 loaded rows and header context in %s flow',
    async (flow) => {
      fixture.componentRef.setInput('rows', dataTableFixtureRows(25));
      fixture.componentRef.setInput('flow', flow);
      await fixture.whenStable();
      expect(root.querySelectorAll('tbody [data-row-id]')).toHaveLength(25);
      expect(root.querySelectorAll('[role="columnheader"]')).toHaveLength(columns.length);
      expect(root.querySelector('.scroll')?.classList.contains('eg-table-long')).toBe(
        flow === 'bounded',
      );
      expect(root.querySelector('cdk-virtual-scroll-viewport')).toBeNull();
    },
  );

  it('retains fixed-height virtualization above 200 despite page-flow and fit opt-ins', async () => {
    fixture.componentRef.setInput('rows', dataTableFixtureRows(201));
    fixture.componentRef.setInput('flow', 'page');
    fixture.componentRef.setInput('fitToContainer', true);
    await fixture.whenStable();
    expect(fixture.componentInstance.virtual()).toBe(true);
    expect(root.querySelector('cdk-virtual-scroll-viewport')).not.toBeNull();
    expect(root.querySelectorAll('[data-row-id]').length).toBeLessThan(201);
  });

  it('keeps legacy column inputs while resolving explicit roles, priorities and overflow', () => {
    const legacy: readonly DataTableColumn<DataTableFixtureRow>[] = [
      { id: 'hash', header: 'Identity', cell: (row) => row.hash, truncate: true },
      { id: 'count', header: 'Attempts', cell: (row) => row.attempts, align: 'end' },
      { id: 'mutation', header: 'Mutation', cell: (row) => row.mutation, mutates: true },
    ];
    expect(
      resolveTableColumns(legacy).map(({ role, priority, overflow }) => ({
        role,
        priority,
        overflow,
      })),
    ).toEqual([
      { role: 'identity', priority: 'primary', overflow: 'truncate' },
      { role: 'number', priority: 'secondary', overflow: 'wrap' },
      { role: 'actions', priority: 'primary', overflow: 'wrap' },
    ]);
    const resolved = resolveTableColumns([
      { ...legacy[0]!, role: 'text', priority: 'secondary', overflow: 'wrap' },
    ]);
    expect(resolved[0]).toMatchObject({ role: 'text', priority: 'secondary', overflow: 'wrap' });
  });

  it('uses one auto-layout native table with subject and numeric header/cell metadata', () => {
    expect(root.querySelectorAll('.table')).toHaveLength(1);
    expect(root.querySelector('.table')?.classList.contains('table-fixed')).toBe(false);
    expect(root.querySelector('th')?.getAttribute('data-column-priority')).toBe('primary');
    for (const cell of root.querySelectorAll('th.numeric,td.numeric')) {
      expect(cell.getAttribute('data-column-role')).toBe('number');
      expect(cell.classList.contains('[&.numeric]:tabular-nums')).toBe(true);
      expect(cell.classList.contains('[&.numeric]:text-right')).toBe(true);
    }
    expect(root.querySelector('td.numeric .value')?.classList.contains('whitespace-nowrap')).toBe(
      true,
    );
  });

  it('retains numeric precision beyond a review budget in stable virtual tracks', () => {
    const precisionColumns: readonly DataTableColumn<DataTableFixtureRow>[] = [
      columns[0]!,
      { id: 'count', header: 'Count', cell: () => '12345678901234567890 ms', role: 'number' },
    ];
    const resolved = resolveTableColumns(precisionColumns, dataTableFixtureRows(1));
    expect(resolved[0]?.virtualWidth).toBeNull();
    expect(resolved[1]?.virtualWidth).toBe('calc(23ch + var(--eg-space-4) * 2)');
  });

  it.each(['light', 'dark'])(
    'preserves named locally sticky header structure in %s theme',
    (theme) => {
      root.setAttribute('data-theme', theme);
      const scroll = root.querySelector('.eg-table-long');
      const header = root.querySelector('.eg-table-sticky');
      expect(scroll?.contains(header)).toBe(true);
      expect(header?.querySelectorAll('th[scope="col"]')).toHaveLength(4);
      expect(header?.closest('cdk-virtual-scroll-viewport')).toBeNull();
      expect(root.classList.contains('bg-card')).toBe(true);
      // jsdom has no real sticky positioning or token cascade; browser QA measures those.
    },
  );

  it('replaces narrow tables with P1 identity/state then every labelled P2 fact', async () => {
    resizeViewport(390);
    const orderedColumns: readonly DataTableColumn<DataTableFixtureRow>[] = [
      columns[1]!,
      { ...columns[2]!, role: 'status', header: 'State / Trạng thái' },
      { ...columns[0]!, priority: 'primary' },
      { ...columns[2]!, id: 'count' },
      columns[3]!,
    ];
    fixture.componentRef.setInput('columns', orderedColumns);
    fixture.componentRef.setInput('rows', [
      { ...dataTableFixtureRows(1)[0]!, note: 'Nguyễn kiểm thử quyền '.repeat(30) },
    ]);
    fixture.componentInstance.narrow.set(true);
    await fixture.whenStable();
    expect(root.querySelector('[role="table"],cdk-virtual-scroll-viewport')).toBeNull();
    const record = root.querySelector('.eg-table-record')!;
    expect(
      Array.from(record.querySelectorAll('dt')).map((label) => label.textContent?.trim()),
    ).toEqual(['State / Trạng thái', 'Evidence identity', 'Context', 'Attempts']);
    expect(record.textContent).toContain('Nguyễn kiểm thử quyền '.repeat(30));
    expect(record.querySelectorAll('dd')).toHaveLength(4);
    expect(record.textContent).not.toContain('Mutation');
    expect(record.querySelector('.truncate,.numeric')).toBeNull();
    expect(record.querySelector('ol')).toBeNull();
    expect(root.querySelector('ol')?.getAttribute('aria-label')).toBe('Evidence / Bằng chứng');
  });

  it('uses the available container width, not only the browser viewport, for stacked records', async () => {
    const width = vi.spyOn(root, 'clientWidth', 'get').mockReturnValue(376);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(3));
    await fixture.whenStable();
    expect(root.querySelectorAll('.eg-table-record')).toHaveLength(3);
    width.mockReturnValue(1000);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(2));
    await fixture.whenStable();
    expect(root.querySelector('.table-semantics')).not.toBeNull();
    expect(root.querySelector('.eg-table-record')).toBeNull();
  });

  it.each([768, 1280, 1440])(
    'retains working actions in a narrow container at a %spx desktop viewport',
    async (width) => {
      const host = await renderActions(width);
      expect(root.querySelector('.eg-table-record')).not.toBeNull();
      expect(root.querySelector('.table-semantics')).toBeNull();
      const actions = root.querySelectorAll<HTMLButtonElement>('[data-test-mutation]');
      expect(actions).toHaveLength(2);
      actions[0]!.click();
      expect(host.componentInstance.calls).toEqual(['demo-0']);
      expect(root.querySelector('.eg-table-record')?.textContent).toContain('Mutation');
      host.destroy();
    },
  );

  it.each([390, 767])('applies the shared viewport mutation lock at %spx', async (width) => {
    const host = await renderActions(width);
    const record = root.querySelector('.eg-table-record');
    expect(record).not.toBeNull();
    expect(record?.textContent).toContain(dataTableFixtureRows(1)[0]!.hash);
    expect(record?.querySelectorAll('dd')).toHaveLength(1);
    expect(root.querySelector('[data-test-mutation]')).toBeNull();
    expect(root.textContent).not.toContain('Mutation');
    expect(host.componentInstance.calls).toEqual([]);
    host.destroy();
  });

  it('restores desktop actions after a phone resize without dropping inspection facts', async () => {
    const host = await renderActions(1280);
    resizeViewport(390);
    await fixture.whenStable();
    expect(root.querySelector('[data-test-mutation]')).toBeNull();
    expect(root.querySelector('.eg-table-record')?.textContent).toContain(
      dataTableFixtureRows(1)[0]!.hash,
    );
    resizeViewport(1280);
    await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('[data-test-mutation]')!.click();
    expect(host.componentInstance.calls).toEqual(['demo-0']);
    host.destroy();
  });

  it('bounds variable-height narrow records at 200 while keeping every loaded record reachable', async () => {
    resizeViewport(390);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(201));
    fixture.componentInstance.narrow.set(true);
    await fixture.whenStable();
    expect(root.querySelectorAll('.eg-table-record')).toHaveLength(200);
    expect(root.querySelector('[role="status"]')?.textContent).toContain(
      'Loaded records 1–200 of 201',
    );
    const buttons = root.querySelectorAll<HTMLButtonElement>('.eg-table-pagination button');
    expect(buttons[0]?.disabled).toBe(true);
    buttons[1]!.click();
    await fixture.whenStable();
    expect(root.querySelectorAll('.eg-table-record')).toHaveLength(1);
    expect(root.querySelector('.eg-table-record')?.getAttribute('data-row-id')).toBe('demo-200');
    expect(root.querySelector('[role="status"]')?.textContent).toContain('201–201 of 201');
    expect(buttons[1]?.disabled).toBe(true);
    expect(root.querySelector('.scroll')?.contains(buttons[0]!)).toBe(false);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(1));
    await fixture.whenStable();
    expect(root.querySelector('.eg-table-record')?.getAttribute('data-row-id')).toBe('demo-0');
    expect(root.querySelector('.eg-table-pagination')).toBeNull();
  });

  it('keeps pager focus on interior pages and hands it to the opposite enabled boundary button', async () => {
    fixture.componentRef.setInput('columns', columns.slice(0, 1));
    fixture.componentRef.setInput('virtualThreshold', 500);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(401));
    await fixture.whenStable();
    const [previous, next] = root.querySelectorAll<HTMLButtonElement>(
      '.eg-table-pagination button',
    );
    next!.focus();
    next!.click();
    await fixture.whenStable();
    expect(next!.disabled).toBe(false);
    expect(document.activeElement).toBe(next);
    expect(root.querySelector('.body-table [data-row-id]')?.getAttribute('data-row-id')).toBe(
      'demo-200',
    );
    next!.click();
    await fixture.whenStable();
    expect(next!.disabled).toBe(true);
    expect(document.activeElement).toBe(previous);
    expect(root.querySelector('.body-table [data-row-id]')?.getAttribute('data-row-id')).toBe(
      'demo-400',
    );
    previous!.click();
    await fixture.whenStable();
    expect(previous!.disabled).toBe(false);
    expect(document.activeElement).toBe(previous);
    expect(root.querySelector('.body-table [data-row-id]')?.getAttribute('data-row-id')).toBe(
      'demo-200',
    );
    previous!.click();
    await fixture.whenStable();
    expect(previous!.disabled).toBe(true);
    expect(document.activeElement).toBe(next);
    expect(root.querySelector('.body-table [data-row-id]')?.getAttribute('data-row-id')).toBe(
      'demo-0',
    );
  });

  it.each([false, true])(
    'does not steal focus when another control owns it before pager rendering (initially focused: %s)',
    async (initiallyFocused) => {
      fixture.componentRef.setInput('virtualThreshold', 500);
      fixture.componentRef.setInput('rows', dataTableFixtureRows(201));
      await fixture.whenStable();
      const next = root.querySelectorAll<HTMLButtonElement>('.eg-table-pagination button')[1]!;
      const elsewhere = document.createElement('button');
      document.body.append(elsewhere);
      try {
        (initiallyFocused ? next : elsewhere).focus();
        next.click();
        elsewhere.focus();
        await fixture.whenStable();
        expect(next.disabled).toBe(true);
        expect(document.activeElement).toBe(elsewhere);
      } finally {
        elsewhere.remove();
      }
    },
  );

  it('uses bounded pages rather than fixed-height virtual rows for arbitrary cell templates', async () => {
    const host = TestBed.createComponent(CellTemplateHost);
    await host.whenStable();
    fixture.componentRef.setInput('columns', [
      { ...columns[1]!, cellTemplate: host.componentInstance.template() },
    ]);
    fixture.componentRef.setInput('rows', dataTableFixtureRows(201));
    await fixture.whenStable();
    expect(root.querySelector('cdk-virtual-scroll-viewport')).toBeNull();
    expect(root.querySelectorAll('.body-table [data-row-id]')).toHaveLength(200);
    expect(root.querySelector('.eg-table-pagination')).not.toBeNull();
    expect(root.querySelector('.body-table')?.textContent).toContain(
      dataTableFixtureRows(1)[0]!.note,
    );
    host.destroy();
  });

  it('grows virtual stride to the text probe at 200% and forces narrative previews into disclosure', async () => {
    const probe = root.querySelector<HTMLElement>('.token-probe')!;
    probe.style.height = '88px';
    fixture.componentRef.setInput('rows', dataTableFixtureRows(201));
    await fixture.whenStable();
    expect(fixture.componentInstance.itemSize()).toBe(88);
    expect(root.querySelector<HTMLElement>('.body-table [data-row-id]')?.style.height).toBe('88px');
    const narrative = root.querySelector('.body-table td[aria-colindex="2"] app-data-table-value');
    expect(narrative?.querySelector('.value')?.classList.contains('truncate')).toBe(true);
    expect(narrative?.classList.contains('overflow-hidden')).toBe(false);
    expect(narrative?.textContent).toContain(dataTableFixtureRows(1)[0]!.note);
    // An 88px model checks stride/disclosure wiring, not actual zoomed glyph geometry.
  });

  it('passes the original machine identifier and a contextual copy label to the shared value family', async () => {
    fixture.componentRef.setInput('columns', DATA_TABLE_FIXTURE_COLUMNS);
    await fixture.whenStable();
    const identifier = fixture.debugElement.query(By.directive(CopyableIdentifierComponent))
      .componentInstance as CopyableIdentifierComponent;
    expect(identifier.value()).toBe(dataTableFixtureRows(1)[0]!.hash);
    expect(identifier.label()).toBe('Evidence identity');
    expect(identifier.truncate()).toBe(true);
  });

  it('keeps raw identifiers at the shared value boundary while rendering escaped and isolated text', async () => {
    const raw = 'sha256:trusted\u202Espoof';
    fixture.componentRef.setInput('columns', DATA_TABLE_FIXTURE_COLUMNS);
    fixture.componentRef.setInput('rows', [{ ...dataTableFixtureRows(1)[0]!, hash: raw }]);
    await fixture.whenStable();
    const identifier = fixture.debugElement.query(By.directive(CopyableIdentifierComponent))
      .componentInstance as CopyableIdentifierComponent;
    expect(identifier.value()).toBe(raw);
    const rendered = root.querySelector('app-copyable-identifier');
    expect(rendered?.querySelector('bdi')?.textContent).toContain('⟨U+202E⟩');
    expect(rendered?.textContent).not.toMatch(/\p{Cf}/u);
  });
});
