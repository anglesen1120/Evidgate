import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  inject,
  input,
  output,
} from '@angular/core';
import type { PolicyRevisionSummaryDto } from '@evidgate/sdk';

@Component({
  selector: 'app-policy-revision-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './policy.page.css',
  template: `
    <section aria-labelledby="policy-revisions-title" [attr.aria-busy]="busy()">
      <h3 id="policy-revisions-title">{{ copy.title }}</h3>
      @if (rows().length === 0) {
        <p>{{ copy.empty }}</p>
      } @else {
        <ol class="revisions">
          @for (row of rows(); track row.id) {
            <li
              data-policy-revision
              [attr.data-revision-no]="row.revision_no"
              [attr.data-revision-state]="row.state"
              [attr.data-policy-head]="row.id === headId() ? '' : null"
            >
              <div class="revision-heading">
                <strong>{{ copy.revision }} {{ row.revision_no }}</strong>
                @if (row.id === headId()) {
                  <span class="badge">{{ copy.head }}</span>
                }
                <span>{{ row.state }}</span>
              </div>
              <dl class="metadata">
                <div>
                  <dt>{{ copy.hash }}</dt>
                  <dd>
                    <code [attr.aria-label]="row.hash">{{ row.hash.slice(0, 12) }}</code>
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
        <button class="pagination" type="button" [disabled]="busy()" (click)="loadMore.emit()">
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
