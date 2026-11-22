import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { PolicyRevisionSummaryDto } from '@evidgate/sdk';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { PolicyRevisionStateComponent } from './policy-revision-state.component';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmTableImports } from '@evidgate/ui/table';

@Component({
  selector: 'app-policy-revision-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmButton,
    HlmTableImports,
    DecimalPipe,
    HlmCardImports,
    AbsoluteTimeComponent,
    StatusPillComponent,
    CopyableIdentifierComponent,
    PolicyRevisionStateComponent,
  ],
  host: { class: 'block min-w-0' },
  template: `
    <section
      hlmCard
      class="min-w-0 gap-0 rounded-lg py-0"
      aria-labelledby="policy-revisions-title"
      [attr.aria-busy]="busy()"
    >
      <header hlmCardHeader class="border-0 border-b border-solid border-border p-(--eg-space-5)">
        <h3 hlmCardTitle [size]="subsectionSize" id="policy-revisions-title">{{ copy.title }}</h3>
      </header>
      <div hlmTableContainer>
        <table
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
                    <button
                      hlmBtn
                      [variant]="'link'"
                      type="button"
                      class="min-h-(--eg-space-7) max-w-full whitespace-normal px-0 py-0 font-semibold tabular-nums"
                      data-policy-select
                      [attr.aria-pressed]="row.id === selectedId()"
                      [attr.aria-controls]="'policy-selected-detail'"
                      [disabled]="busy()"
                      (click)="select(row.id)"
                    >
                      {{ copy.revision }} {{ row.revision_no }}
                    </button>
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
              </tr>
            } @empty {
              <tr hlmTr [attr.role]="'row'">
                <td hlmTd [attr.role]="'cell'" colspan="3">
                  <p class="m-0 text-muted-foreground">{{ copy.empty }}</p>
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
      @if (rows().length || hasMore()) {
        <footer
          hlmCardFooter
          class="flex min-w-0 flex-wrap items-center justify-between gap-(--eg-space-3) border-0 border-t border-solid border-border p-(--eg-space-5)"
        >
          @if (rows().length) {
            <span
              class="text-xs tabular-nums text-muted-foreground"
              i18n="@@settings.policy.history-range"
              >Showing 1–{{ rows().length | number }} loaded revisions</span
            >
          }
          @if (hasMore()) {
            <button
              hlmBtn
              [variant]="'outline'"
              class="min-h-(--eg-control-touch) whitespace-normal"
              type="button"
              data-policy-more
              [disabled]="busy()"
              (click)="loadMore.emit()"
            >
              {{ copy.more }}
            </button>
          }
        </footer>
      }
    </section>
  `,
})
export class RevisionListComponent {
  readonly rows = input<readonly PolicyRevisionSummaryDto[]>([]);
  readonly headId = input<string | null>(null);
  readonly selectedId = input<string | null>(null);
  readonly selected = output<string>();
  protected readonly subsectionSize = 'subsection' as const;
  protected readonly headTone = 'info' as const;
  readonly hasMore = input(false);
  readonly busy = input(false);
  readonly loadMore = output<void>();
  protected select(id: string): void {
    if (!this.busy() && this.rows().some((row) => row.id === id)) this.selected.emit(id);
  }
  protected readonly copy = {
    title: $localize`:@@settings.policy.revision-history:Revision history`,
    empty: $localize`:@@settings.policy.revisions-empty:No revisions available.`,
    revision: $localize`:@@settings.policy.revision:Revision`,
    state: $localize`:@@settings.policy.state:Revision state`,
    head: $localize`:@@settings.policy.head:Head`,
    hash: $localize`:@@settings.policy.hash:Hash`,
    created: $localize`:@@settings.policy.created:Created`,
    more: $localize`:@@settings.policy.more:Load more`,
  };
}
