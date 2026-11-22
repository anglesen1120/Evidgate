import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { HlmCard } from '@evidgate/ui/card';
import { HlmTableImports } from '@evidgate/ui/table';
import { INBOX_COPY } from './failure-inbox.copy';
import { CANDIDATE } from './failure-inbox.fixture';
import { FailureInboxStore } from './failure-inbox.store';

@Component({
  selector: 'app-inbox-summary',
  imports: [HlmCard, HlmTableImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <details hlmCard class="block min-w-0 p-4">
      <summary class="cursor-pointer font-semibold">{{ c.table }}</summary>
      <div
        hlmTableContainer
        role="region"
        tabindex="0"
        [attr.aria-label]="c.table"
        class="mt-2 max-w-full overflow-x-auto"
      >
        <table hlmTable>
          <caption class="sr-only">
            {{
              c.table
            }}
          </caption>
          <thead hlmTHead>
            <tr hlmTr>
              @for (heading of headings; track heading) {
                <th hlmTh scope="col">{{ heading }}</th>
              }
            </tr>
          </thead>
          <tbody hlmTBody>
            @for (row of store.matches(); track row.id) {
              <tr hlmTr>
                <th hlmTh scope="row">{{ row.id }}</th>
                <td hlmTd>{{ c.raw }}</td>
                <td hlmTd>{{ c[row.severity] }}</td>
                <td hlmTd>{{ row.label }} · {{ c[row.source] }}</td>
                <td hlmTd>{{ c[row.owner] }}</td>
                <td hlmTd>{{ row.revision }} · {{ c[row.context] }}</td>
                <td hlmTd>{{ row.id === 'inc-92' ? candidate : c.absent }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    </details>
  `,
})
export class InboxSummaryComponent {
  protected readonly store = inject(FailureInboxStore);
  protected readonly c = INBOX_COPY;
  protected readonly candidate = CANDIDATE;
  protected readonly headings = [
    this.c.incident,
    this.c.outcome,
    this.c.severity,
    this.c.labelSource,
    this.c.owner,
    this.c.revContext,
    this.c.count,
  ];
}
