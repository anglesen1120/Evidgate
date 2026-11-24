import { LoadingPatternComponent } from '../../../shared/ui/states/loading-pattern.component';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { PolicyRevisionSummaryDto } from '@evidgate/sdk';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { DataTablePagerComponent } from '../../../shared/ui/data-table/data-table-pager.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { RowActionComponent } from '../../../shared/ui/data-table/row-action.component';
import { PolicyRevisionStateComponent } from './policy-revision-state.component';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmTableImports } from '@evidgate/ui/table';

@Component({
  selector: 'app-policy-revision-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    LoadingPatternComponent,
    RowActionComponent,
    HlmTableImports,
    DataTablePagerComponent,
    HlmCardImports,
    AbsoluteTimeComponent,
    StatusPillComponent,
    CopyableIdentifierComponent,
    PolicyRevisionStateComponent,
  ],
  host: { class: 'block min-w-0' },
  template: `
    <section hlmCard class="min-w-0 gap-0 rounded-lg py-0" aria-labelledby="policy-revisions-title">
      <header
        [attr.aria-busy]="busy()"
        hlmCardHeader
        class="border-0 border-b border-solid border-border p-(--eg-space-5)"
      >
        <h3 hlmCardTitle [size]="subsectionSize" id="policy-revisions-title">{{ copy.title }}</h3>
        <p hlmCardDescription>{{ copy.order }}</p>
      </header>
      <app-loading-pattern
        [layout]="'table'"
        [busy]="fetching()"
        [hasRows]="rows().length > 0"
        [announceProgress]="false"
      />
      <div hlmTableContainer>
        <table
          [attr.aria-busy]="fetching()"
          hlmTable
          class="w-full table-fixed border-collapse max-md:block [&_td]:whitespace-normal [&_th]:whitespace-normal"
          aria-labelledby="policy-revisions-title"
          [attr.role]="'table'"
        >
          <thead
            hlmTHead
            [attr.role]="'rowgroup'"
            class="bg-(--eg-surface-raised) text-xs max-md:sr-only"
          >
            <tr hlmTr [attr.role]="'row'">
              <th hlmTh [attr.role]="'columnheader'" scope="col" class="w-2/5">
                {{ copy.revision }}
              </th>
              <th hlmTh [attr.role]="'columnheader'" scope="col">{{ copy.state }}</th>
              <th hlmTh [attr.role]="'columnheader'" scope="col">{{ copy.created }}</th>
              <th hlmTh [attr.role]="'columnheader'" scope="col" class="text-end">
                {{ copy.actions }}
              </th>
            </tr>
          </thead>
          <tbody hlmTBody [attr.role]="'rowgroup'" class="max-md:block">
            @for (row of rows(); track row.id) {
              <tr
                hlmTr
                [attr.role]="'row'"
                class="h-(--eg-space-12) border-0 border-t border-solid border-border data-[policy-selected]:bg-(--eg-accent-soft) max-md:grid max-md:h-auto max-md:gap-(--eg-space-3) max-md:p-(--eg-space-4)"
                data-policy-revision
                [attr.data-revision-no]="row.revision_no"
                [attr.data-revision-state]="row.state"
                [attr.data-policy-head]="row.id === headId() ? '' : null"
                [attr.data-policy-selected]="row.id === selectedId() ? '' : null"
              >
                <td hlmTd [attr.role]="'cell'" class="min-w-0 py-(--eg-space-1)">
                  <div class="flex flex-wrap items-center gap-(--eg-space-3)">
                    <span class="font-semibold tabular-nums"
                      >{{ copy.revision }} {{ row.revision_no }}</span
                    >
                    @if (row.id === headId()) {
                      <app-status-pill [tone]="headTone" [label]="copy.head" />
                    }
                  </div>
                  <app-copyable-identifier
                    class="block text-xs text-muted-foreground"
                    [value]="row.hash"
                    [label]="copy.hash + ' · ' + copy.revision + ' ' + row.revision_no"
                    [compact]="true"
                  />
                </td>
                <td hlmTd [attr.role]="'cell'">
                  <span class="block text-xs text-muted-foreground md:hidden">{{
                    copy.state
                  }}</span>
                  <app-policy-revision-state [state]="row.state" />
                </td>
                <td hlmTd [attr.role]="'cell'">
                  <span class="block text-xs text-muted-foreground md:hidden">{{
                    copy.created
                  }}</span>
                  <app-absolute-time [value]="row.created_at" />
                </td>
                <td hlmTd [attr.role]="'cell'" class="text-end">
                  <button
                    appRowAction
                    #trigger
                    [label]="copy.view + ' · ' + copy.revision + ' ' + row.revision_no"
                    type="button"
                    [attr.aria-haspopup]="'dialog'"
                    data-policy-view
                    [attr.aria-label]="copy.view + ' · ' + copy.revision + ' ' + row.revision_no"
                    [disabled]="busy()"
                    (click)="select(row.id, trigger.nativeElement)"
                  ></button>
                </td>
              </tr>
            } @empty {
              <tr hlmTr [attr.role]="'row'">
                <td hlmTd [attr.role]="'cell'" colspan="4">
                  <p class="m-0 text-muted-foreground">{{ copy.empty }}</p>
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
      <footer
        hlmCardFooter
        class="block min-w-0 border-0 border-t border-solid border-border p-(--eg-space-5)"
      >
        <app-data-table-pager
          [page]="page()"
          [pageSize]="pageSize()"
          [count]="rows().length"
          [total]="null"
          [hasNext]="hasMore()"
          [busy]="busy()"
          (previous)="previous.emit()"
          (next)="next.emit()"
          (pageSizeChange)="pageSizeChange.emit($event)"
        />
      </footer>
    </section>
  `,
})
export class RevisionListComponent {
  readonly fetching = input(false);
  readonly rows = input<readonly PolicyRevisionSummaryDto[]>([]);
  readonly headId = input<string | null>(null);
  readonly selectedId = input<string | null>(null);
  readonly selected = output<{ id: string; trigger: HTMLElement }>();
  protected readonly subsectionSize = 'subsection' as const;
  protected readonly headTone = 'info' as const;
  readonly hasMore = input(false);
  readonly busy = input(false);
  readonly page = input(1);
  readonly pageSize = input(20);
  readonly previous = output<void>();
  readonly next = output<void>();
  readonly pageSizeChange = output<number>();
  protected select(id: string, trigger: HTMLElement): void {
    if (!this.busy() && this.rows().some((row) => row.id === id))
      this.selected.emit({ id, trigger });
  }
  protected readonly copy = {
    title: $localize`:@@settings.policy.revision-history:Revision history`,
    order: $localize`:@@settings.policy.history-order:Oldest first`,
    view: $localize`:@@settings.row.view:View`,
    actions: $localize`:@@settings.row.actions:Actions`,
    empty: $localize`:@@settings.policy.revisions-empty:No revisions available.`,
    revision: $localize`:@@settings.policy.revision:Revision`,
    state: $localize`:@@settings.policy.state:Revision state`,
    head: $localize`:@@settings.policy.head:Head`,
    hash: $localize`:@@settings.policy.hash:Hash`,
    created: $localize`:@@settings.policy.created:Created`,
  };
}
