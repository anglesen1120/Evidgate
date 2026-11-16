import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { PolicyRevisionSummaryDto } from '@evidgate/sdk';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { PolicyRevisionStateComponent } from './policy-revision-state.component';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';

@Component({
  selector: 'app-policy-revision-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmButton,
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
      class="gap-4 p-4"
      aria-labelledby="policy-revisions-title"
      [attr.aria-busy]="busy()"
    >
      <h3 hlmCardTitle [size]="subsectionSize" id="policy-revisions-title">{{ copy.title }}</h3>
      @if (rows().length === 0) {
        <p class="m-0 text-muted-foreground">{{ copy.empty }}</p>
      } @else {
        <ol class="m-0 grid list-none gap-0 p-0">
          @for (row of rows(); track row.id) {
            <li
              class="grid min-w-0 gap-3 border-0 border-t border-solid border-border py-4 first:border-t-0 first:pt-0 last:pb-0"
              data-policy-revision
              [attr.data-revision-no]="row.revision_no"
              [attr.data-revision-state]="row.state"
              [attr.data-policy-head]="row.id === headId() ? '' : null"
              [attr.data-policy-selected]="row.id === selectedId() ? '' : null"
            >
              <div class="flex flex-wrap items-center gap-2">
                <button
                  hlmBtn
                  [variant]="'outline'"
                  type="button"
                  class="min-h-(--eg-control-touch) max-w-full whitespace-normal"
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
                <app-policy-revision-state [state]="row.state" />
              </div>
              <dl
                class="m-0 grid gap-3 [&_dd]:m-0 [&_dd]:wrap-anywhere [&_dt]:text-xs [&_dt]:text-muted-foreground"
              >
                <div>
                  <dt>{{ copy.hash }}</dt>
                  <dd>
                    <app-copyable-identifier
                      [value]="row.hash"
                      [label]="copy.hash + ' · ' + copy.revision + ' ' + row.revision_no"
                    />
                  </dd>
                </div>
                <div>
                  <dt>{{ copy.created }}</dt>
                  <dd>
                    <app-absolute-time [value]="row.created_at" />
                  </dd>
                </div>
              </dl>
            </li>
          }
        </ol>
      }
      @if (hasMore()) {
        <button
          hlmBtn
          [variant]="'outline'"
          class="min-h-[var(--eg-control-touch)] whitespace-normal"
          type="button"
          data-policy-more
          [disabled]="busy()"
          (click)="loadMore.emit()"
        >
          {{ copy.more }}
        </button>
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
    title: $localize`:@@settings.policy.revisions:Revisions`,
    empty: $localize`:@@settings.policy.revisions-empty:No revisions available.`,
    revision: $localize`:@@settings.policy.revision:Revision`,
    head: $localize`:@@settings.policy.head:Head`,
    hash: $localize`:@@settings.policy.hash:Hash`,
    created: $localize`:@@settings.policy.created:Created`,
    more: $localize`:@@settings.policy.more:Load more`,
  };
}
