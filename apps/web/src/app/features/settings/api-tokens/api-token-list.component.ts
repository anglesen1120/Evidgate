import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { ApiTokenView } from '@evidgate/sdk';
import { HlmTableImports } from '@evidgate/ui/table';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { API_TOKENS_WRITE_COPY } from './api-tokens-write.copy';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { API_TOKENS_COPY } from './api-tokens.copy';

@Component({
  selector: 'app-api-token-list',
  imports: [HlmTableImports, HlmBadge, HlmButton, StatusPillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <div
      hlmTableContainer
      class="min-w-0 max-w-full rounded-md border border-border bg-card max-md:overflow-visible"
    >
      <table
        hlmTable
        role="table"
        class="w-full table-fixed caption-top max-md:block [&_tbody]:max-md:block [&_tr]:max-md:block [&_thead]:max-md:sr-only [&_th]:whitespace-normal [&_td]:whitespace-normal [&_th]:wrap-anywhere [&_td]:wrap-anywhere [&_tbody_th]:max-md:block [&_tbody_td]:max-md:block"
      >
        <caption hlmCaption class="p-3 text-start font-semibold text-foreground max-md:block">
          {{
            copy.directory
          }}
        </caption>
        <thead hlmTHead role="rowgroup" class="bg-secondary">
          <tr hlmTr role="row">
            <th hlmTh role="columnheader" scope="col" id="api-name">{{ copy.name }}</th>
            <th hlmTh role="columnheader" scope="col" id="api-prefix">{{ copy.prefix }}</th>
            <th hlmTh role="columnheader" scope="col" id="api-permissions">
              {{ copy.permissions }}
            </th>
            <th hlmTh role="columnheader" scope="col" id="api-expires">{{ copy.expires }}</th>
            <th hlmTh role="columnheader" scope="col" id="api-state">{{ copy.state }}</th>
            <th hlmTh role="columnheader" scope="col" id="api-used">{{ copy.lastUsed }}</th>
            @if (canWrite()) {
              <th hlmTh role="columnheader" scope="col" class="hidden md:table-cell">
                {{ writeCopy.actions }}
              </th>
            }
          </tr>
        </thead>
        <tbody hlmTBody role="rowgroup">
          @for (row of rows(); track row.id) {
            <tr hlmTr role="row" data-api-token-row [attr.data-state]="row.status">
              <th hlmTh scope="row" role="rowheader" [id]="'api-row-' + row.id">
                <span class="block text-xs text-muted-foreground md:hidden">{{ copy.name }}</span>
                {{ row.name }}
              </th>
              <td hlmTd role="cell" [attr.headers]="'api-row-' + row.id + ' api-prefix'">
                <span class="block text-xs text-muted-foreground md:hidden">{{ copy.prefix }}</span>
                <code class="font-mono">{{ row.prefix }}</code>
              </td>
              <td hlmTd role="cell" [attr.headers]="'api-row-' + row.id + ' api-permissions'">
                <span class="block text-xs text-muted-foreground md:hidden">{{
                  copy.permissions
                }}</span>
                <div class="flex flex-wrap gap-1">
                  @for (permission of row.permissions; track permission) {
                    <span
                      hlmBadge
                      [variant]="'secondary'"
                      class="max-w-full whitespace-normal wrap-anywhere"
                      >{{ permission }}</span
                    >
                  }
                </div>
              </td>
              <td hlmTd role="cell" [attr.headers]="'api-row-' + row.id + ' api-expires'">
                <span class="block text-xs text-muted-foreground md:hidden">{{
                  copy.expires
                }}</span>
                <time [attr.datetime]="row.expires_at">{{ row.expires_at }}</time>
              </td>
              <td hlmTd role="cell" [attr.headers]="'api-row-' + row.id + ' api-state'">
                <span class="block text-xs text-muted-foreground md:hidden">{{ copy.state }}</span>
                <app-status-pill
                  [tone]="row.status === 'active' ? 'success' : 'neutral'"
                  [label]="stateLabel(row.status)"
                />
              </td>
              <td hlmTd role="cell" [attr.headers]="'api-row-' + row.id + ' api-used'">
                <span class="block text-xs text-muted-foreground md:hidden">{{
                  copy.lastUsed
                }}</span>
                @if (row.last_used_at; as used) {
                  <time [attr.datetime]="used">{{ used }}</time>
                } @else {
                  {{ copy.never }}
                }
              </td>
              @if (canWrite()) {
                <td hlmTd role="cell" class="max-md:hidden! md:table-cell">
                  @if (row.status !== 'revoked') {
                    <button
                      hlmBtn
                      [variant]="'outline'"
                      type="button"
                      data-api-revoke
                      [attr.data-row-id]="row.id"
                      [attr.aria-label]="writeCopy.revoke + ': ' + row.name"
                      class="h-auto min-h-[var(--eg-control-touch)] max-w-full whitespace-normal wrap-anywhere"
                      [disabled]="busy()"
                      (click)="revoke.emit(row)"
                    >
                      {{ writeCopy.revoke }}
                    </button>
                  }
                </td>
              }
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
})
export class ApiTokenListComponent {
  readonly rows = input<readonly ApiTokenView[]>([]);
  protected readonly copy = API_TOKENS_COPY;
  readonly canWrite = input(false);
  readonly busy = input(false);
  readonly revoke = output<ApiTokenView>();
  protected readonly writeCopy = API_TOKENS_WRITE_COPY;

  protected stateLabel(state: ApiTokenView['status']): string {
    switch (state) {
      case 'active':
        return this.copy.active;
      case 'expired':
        return this.copy.expired;
      case 'revoked':
        return this.copy.revoked;
      default:
        return state;
    }
  }
}
