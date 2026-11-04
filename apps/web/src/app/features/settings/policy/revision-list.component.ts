import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  inject,
  input,
  output,
} from '@angular/core';
import type { PolicyRevisionSummaryDto } from '@evidgate/sdk';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';

@Component({
  selector: 'app-policy-revision-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmBadge, HlmButton, HlmCardImports],
  host: { class: 'block min-w-0' },
  template: `
    <section
      hlmCard
      class="gap-4 p-4"
      aria-labelledby="policy-revisions-title"
      [attr.aria-busy]="busy()"
    >
      <h3 hlmCardTitle id="policy-revisions-title" class="m-0 text-base">{{ copy.title }}</h3>
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
            >
              <div class="flex flex-wrap items-center gap-2">
                <strong>{{ copy.revision }} {{ row.revision_no }}</strong>
                @if (row.id === headId()) {
                  <span hlmBadge [variant]="'secondary'">{{ copy.head }}</span>
                }
                <span hlmBadge [variant]="'outline'">{{ row.state }}</span>
              </div>
              <dl
                class="m-0 grid gap-3 [&_dd]:m-0 [&_dd]:wrap-anywhere [&_dt]:text-xs [&_dt]:text-muted-foreground"
              >
                <div>
                  <dt>{{ copy.hash }}</dt>
                  <dd>
                    <code class="font-mono" [attr.aria-label]="row.hash">{{
                      row.hash.slice(0, 12)
                    }}</code>
                  </dd>
                </div>
                <div>
                  <dt>{{ copy.created }}</dt>
                  <dd>
                    <time [attr.datetime]="row.created_at">{{ formatDate(row.created_at) }}</time>
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
  readonly hasMore = input(false);
  readonly busy = input(false);
  readonly loadMore = output<void>();
  private readonly locale = inject(LOCALE_ID);
  private readonly dates = new Intl.DateTimeFormat(this.locale, {
    dateStyle: 'medium',
    timeStyle: 'short',
  });
  protected readonly copy = {
    title: $localize`:@@settings.policy.revisions:Revisions`,
    empty: $localize`:@@settings.policy.revisions-empty:No revisions available.`,
    revision: $localize`:@@settings.policy.revision:Revision`,
    head: $localize`:@@settings.policy.head:Head`,
    hash: $localize`:@@settings.policy.hash:Hash`,
    created: $localize`:@@settings.policy.created:Created`,
    more: $localize`:@@settings.policy.more:Load more`,
  };
  protected formatDate(value: string): string {
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? value : this.dates.format(date);
  }
}
