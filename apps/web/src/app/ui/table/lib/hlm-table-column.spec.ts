import { ChangeDetectionStrategy, Component, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { HlmTableImports } from '../index';

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
        <thead hlmTHead>
          <tr hlmTr>
            <th hlmTh scope="col" [hlmTableColumn]="'identity'" [columnPriority]="'primary'">
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
}

describe('HlmTableColumn', () => {
  it('exposes role budgets without replacing native Helm table semantics', async () => {
    await TestBed.configureTestingModule({
      imports: [NativeColumnHost],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(NativeColumnHost);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
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
    // CSS performs intrinsic sizing, sticky layout and token resolution; jsdom cannot measure it.
  });
});
