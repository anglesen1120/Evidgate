import { TestBed } from '@angular/core/testing';

import { StatusPillComponent } from '../chips/status-pill.component';
import { CopyValueButtonComponent } from '../values/copy-value-button.component';
import { DataTableValueComponent } from './data-table-value.component';

/** Keep real table/cell templates and paging; omit unrelated leaf decoration in volume tests. */
export function useLightweightTablePresentation(): void {
  TestBed.overrideComponent(DataTableValueComponent, {
    set: { template: '<span #valueElement>{{ displayValue() }}</span>' },
  });
  TestBed.overrideComponent(StatusPillComponent, {
    set: {
      template: `
        <span>{{ displayLabel() }}</span>
        @if (detail()) {
          <span class="status-pill__detail">{{ detail() }}</span>
        }
      `,
    },
  });
  TestBed.overrideComponent(CopyValueButtonComponent, {
    set: {
      template: `
        <button type="button" [attr.aria-label]="actionLabel()" (click)="copy()">
          {{ buttonText() }}
        </button>
      `,
    },
  });
}
