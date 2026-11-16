import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  signal,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { HlmTableImports, type DataTableColumnWidth } from '../index';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmTableImports],
  template: `
    <div hlmTableContainer class="eg-table-long" role="region" [attr.aria-label]="records">
      <table hlmTable class="eg-table-layout">
        <caption hlmCaption>
          {{
            records
          }}
        </caption>
        <colgroup>
          <col [hlmTableColumn]="'identity'" [width]="width()" />
        </colgroup>
        <thead hlmTHead>
          <tr hlmTr>
            <th
              hlmTh
              scope="col"
              [hlmTableColumn]="'identity'"
              [columnPriority]="'primary'"
              [width]="width()"
            >
              {{ subject }}
            </th>
            <th hlmTh scope="col" [hlmTableColumn]="'number'">{{ count }}</th>
            <th hlmTh scope="col" [hlmTableColumn]="'date'">{{ recordedAt }}</th>
          </tr>
        </thead>
        <tbody hlmTBody>
          <tr hlmTr>
            <td
              hlmTd
              [hlmTableColumn]="'identity'"
              [columnKind]="'email'"
              [columnPriority]="'primary'"
              [columnOverflow]="'truncate'"
              [width]="width()"
              [style.min-inline-size]="templateWidth()?.min"
              [style.inline-size]="templateWidth()?.preferred"
              [style.max-inline-size]="templateWidth()?.max"
            >
              {{ email }}
            </td>
            <td hlmTd [hlmTableColumn]="'number'">0</td>
            <td hlmTd [hlmTableColumn]="'date'">{{ timestamp }}</td>
          </tr>
        </tbody>
      </table>
    </div>
  `,
})
class NativeColumnHost {
  readonly records = 'Records';
  readonly subject = 'Subject';
  readonly count = 'Count';
  readonly recordedAt = 'Recorded at UTC';
  readonly email = 'nguyen@example.test';
  readonly timestamp = 'November 14, 2026 UTC';
  readonly width = signal<DataTableColumnWidth | undefined>(undefined);
  readonly templateWidth = signal<DataTableColumnWidth | undefined>(undefined);
}

async function render(width?: DataTableColumnWidth) {
  await TestBed.configureTestingModule({
    imports: [NativeColumnHost],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(NativeColumnHost);
  fixture.componentInstance.width.set(width);
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement };
}

function expectNoWidthOverrides(column: HTMLElement): void {
  for (const property of ['min-inline-size', 'inline-size', 'max-inline-size'])
    expect(column.style.getPropertyValue(property)).toBe('');
}

describe('HlmTableColumn', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('exposes role budgets without replacing native Helm table semantics', async () => {
    const { root } = await render();
    expect(root.querySelector('table')?.getAttribute('data-slot')).toBe('table');
    expect(root.querySelector('thead')?.getAttribute('data-slot')).toBe('table-header');
    expect(root.querySelector('th')?.getAttribute('scope')).toBe('col');
    expect(root.querySelector('td')?.getAttribute('data-column-kind')).toBe('email');
    expect(root.querySelector('td')?.getAttribute('data-column-priority')).toBe('primary');
    expect(root.querySelector('td')?.getAttribute('data-column-overflow')).toBe('truncate');
    expect(root.querySelectorAll('[data-column-role="number"]')).toHaveLength(2);
    expect(root.querySelectorAll('[data-column-role="date"]')).toHaveLength(2);
    expect(root.querySelector('.eg-table-long')?.getAttribute('aria-label')).toBe('Records');
    expect(root.querySelector('[role="grid"]')).toBeNull();
    for (const column of root.querySelectorAll<HTMLElement>('[hlmTableColumn]'))
      expectNoWidthOverrides(column);
    // CSS performs intrinsic sizing, sticky layout and token resolution; jsdom cannot measure it.
  });

  it('applies one optional width contract to native col, th and td hosts', async () => {
    const { root } = await render({ min: '18ch', preferred: '1%', max: '28ch' });
    const columns = root.querySelectorAll<HTMLElement>('[data-column-role="identity"]');
    expect(columns).toHaveLength(3);
    for (const column of columns) {
      expect(column.style.getPropertyValue('min-inline-size')).toBe('18ch');
      expect(column.style.getPropertyValue('inline-size')).toBe('1%');
      expect(column.style.getPropertyValue('max-inline-size')).toBe('28ch');
      expect(column.hasAttribute('width')).toBe(false);
    }
    const number = root.querySelector<HTMLElement>('td[data-column-role="number"]');
    if (!number) throw new Error('Expected numeric cell');
    expectNoWidthOverrides(number);
  });

  it('keeps omitted bounds independent and removes overrides when width is cleared', async () => {
    const { fixture, root } = await render({ min: '10ch' });
    const column = root.querySelector<HTMLElement>('td[data-column-role="identity"]');
    if (!column) throw new Error('Expected identity cell');
    expect(column.style.getPropertyValue('min-inline-size')).toBe('10ch');
    expect(column.style.getPropertyValue('inline-size')).toBe('');
    expect(column.style.getPropertyValue('max-inline-size')).toBe('');
    fixture.componentInstance.width.set({ preferred: 'max-content', max: '28ch' });
    await fixture.whenStable();
    expect(column.style.getPropertyValue('min-inline-size')).toBe('');
    expect(column.style.getPropertyValue('inline-size')).toBe('max-content');
    expect(column.style.getPropertyValue('max-inline-size')).toBe('28ch');
    column.style.setProperty('text-align', 'start');
    fixture.componentInstance.width.set(undefined);
    await fixture.whenStable();
    expectNoWidthOverrides(column);
    expect(column.style.getPropertyValue('text-align')).toBe('start');
    expect(column.getAttribute('data-column-kind')).toBe('email');
    expect(column.getAttribute('data-column-priority')).toBe('primary');
  });

  it('preserves existing template width bindings ahead of optional directive widths', async () => {
    const { fixture, root } = await render();
    fixture.componentInstance.templateWidth.set({ min: '12ch', preferred: 'auto', max: '36ch' });
    fixture.componentInstance.width.set({ min: '18ch', preferred: '1%', max: '28ch' });
    await fixture.whenStable();
    const cell = root.querySelector<HTMLElement>('td[data-column-role="identity"]');
    if (!cell) throw new Error('Expected identity cell');
    expect(cell.style.getPropertyValue('min-inline-size')).toBe('12ch');
    expect(cell.style.getPropertyValue('inline-size')).toBe('auto');
    expect(cell.style.getPropertyValue('max-inline-size')).toBe('36ch');
    expect(root.querySelector<HTMLElement>('th')?.style.getPropertyValue('min-inline-size')).toBe(
      '18ch',
    );
    fixture.componentInstance.width.set(undefined);
    await fixture.whenStable();
    expect(cell.style.getPropertyValue('min-inline-size')).toBe('12ch');
    expect(cell.style.getPropertyValue('inline-size')).toBe('auto');
    expect(cell.style.getPropertyValue('max-inline-size')).toBe('36ch');
  });
});
