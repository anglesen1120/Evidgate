import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import type { ApiTokenView } from '@evidgate/sdk';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmButton } from '@evidgate/ui/button';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmTableImports } from '@evidgate/ui/table';
import {
  SettingsPageCountsComponent,
  type SettingsPageCount,
} from '../settings-page-counts.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { DataTableValueComponent } from '../../../shared/ui/data-table/data-table-value.component';
import { DesktopMutationDirective } from '../../../shared/ui/narrow/desktop-mutation.directive';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { EmptyPatternComponent } from '../../../shared/ui/states/empty-pattern.component';
import { ApiTokenFactsComponent, apiTokenStateLabel } from './api-token-facts.component';
import { ApiTokenPermissionTagsComponent } from './api-token-permission-tags.component';
import { API_TOKENS_WRITE_COPY } from './api-tokens-write.copy';
import { API_TOKENS_COPY } from './api-tokens.copy';

@Component({
  selector: 'app-api-token-list',
  imports: [
    NgTemplateOutlet,
    HlmTableImports,
    SettingsPageCountsComponent,
    HlmCardImports,
    HlmButton,
    HlmInput,
    HlmLabel,
    StatusPillComponent,
    DataTableValueComponent,
    ApiTokenFactsComponent,
    ApiTokenPermissionTagsComponent,
    DesktopMutationDirective,
    EmptyPatternComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: '@container block min-w-0' },
  template: `
    <app-settings-page-counts
      class="mb-6"
      [attr.data-token-page-count]="''"
      [metrics]="pageCounts()"
    />
    <ng-template #scanName let-row>
      <button
        hlmBtn
        [variant]="'link'"
        class="h-auto max-w-full justify-start px-0 py-1 text-start whitespace-normal pointer-coarse:min-h-(--eg-control-touch)"
        type="button"
        data-api-inspect
        [attr.aria-pressed]="selected()?.id === row.id"
        [attr.aria-label]="inspectLabel(row.name)"
        (click)="selectedId.set(row.id)"
      >
        <app-data-table-value class="min-w-0 font-bold" [value]="row.name" />
      </button>
      <app-api-token-permission-tags [permissions]="row.permissions" [name]="row.name" />
      @if (canWrite() && row.status !== 'revoked') {
        <button
          *appDesktopMutation
          hlmBtn
          [variant]="'ghost'"
          type="button"
          data-api-revoke
          [attr.data-row-id]="row.id"
          [attr.aria-label]="writeCopy.revoke + ': ' + row.name"
          [disabled]="busy()"
          (click)="requestRevoke(row)"
        >
          {{ writeCopy.revoke }}
        </button>
      }
    </ng-template>
    <div
      class="grid min-w-0 grid-cols-[minmax(0,1fr)] items-start gap-4 min-[1025px]:@min-[45rem]:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)]"
    >
      <section hlmCard class="min-w-0 gap-0 rounded-xl p-0" aria-labelledby="api-directory-title">
        <header hlmCardHeader class="min-w-0 gap-3 border-b border-border px-5 py-4">
          <h3 hlmCardTitle [size]="'subsection'" id="api-directory-title" class="m-0">
            {{ copy.directory }}
          </h3>
          <div class="grid min-w-0 gap-1">
            <label hlmLabel for="api-token-search">{{ searchLabel }}</label>
            <input
              hlmInput
              id="api-token-search"
              type="search"
              [value]="query()"
              [aria-describedby]="'api-token-search-hint'"
              (input)="search($event)"
            />
            <p id="api-token-search-hint" class="m-0 text-xs text-muted-foreground">
              {{ loadedOnly }}
            </p>
          </div>
        </header>
        @if (filtered().length) {
          @if (viewport.narrow()) {
            <ul class="m-0 grid min-w-0 list-none p-0" [attr.aria-label]="copy.directory">
              @for (row of filtered(); track row.id) {
                <li
                  class="min-w-0 border-b border-border p-4 last:border-0 data-[selected=true]:bg-(--eg-accent-soft)"
                  data-api-token-row
                  [attr.data-state]="row.status"
                  [attr.data-selected]="selected()?.id === row.id"
                >
                  <ng-container
                    [ngTemplateOutlet]="scanName"
                    [ngTemplateOutletContext]="{ $implicit: row }"
                  />
                  <app-api-token-facts class="mt-3" [row]="row" />
                </li>
              }
            </ul>
          } @else {
            <div hlmTableContainer>
              <table
                hlmTable
                class="w-full table-fixed border-collapse"
                aria-labelledby="api-directory-title"
              >
                <thead hlmTHead class="bg-(--eg-surface-raised) text-xs">
                  <tr hlmTr>
                    <th hlmTh scope="col">{{ copy.name }}</th>
                    <th hlmTh scope="col" class="w-[24%] whitespace-normal">{{ copy.prefix }}</th>
                    <th hlmTh scope="col" class="w-[24%] whitespace-normal">{{ copy.state }}</th>
                  </tr>
                </thead>
                <tbody hlmTBody>
                  @for (row of filtered(); track row.id) {
                    <tr
                      hlmTr
                      class="data-[selected=true]:bg-(--eg-accent-soft)"
                      data-api-token-row
                      [attr.data-state]="row.status"
                      [attr.data-selected]="selected()?.id === row.id"
                    >
                      <td hlmTd class="whitespace-normal">
                        <ng-container
                          [ngTemplateOutlet]="scanName"
                          [ngTemplateOutletContext]="{ $implicit: row }"
                        />
                      </td>
                      <td hlmTd class="font-mono text-xs whitespace-normal wrap-anywhere">
                        {{ row.prefix }}
                      </td>
                      <td hlmTd>
                        <app-status-pill
                          [tone]="row.status === 'active' ? 'success' : 'neutral'"
                          [label]="stateLabel(row.status)"
                        />
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        } @else {
          <div class="grid justify-items-start gap-3 px-5 py-4">
            <app-empty-pattern [bare]="true" [title]="noMatches" [description]="loadedOnly" />
            <button hlmBtn [variant]="'outline'" type="button" (click)="query.set('')">
              {{ clearSearch }}
            </button>
          </div>
        }
        <ng-content />
      </section>
      @if (selected(); as row) {
        <section
          hlmCard
          class="min-w-0 gap-0 rounded-xl p-0"
          aria-labelledby="api-detail-title"
          data-api-token-detail
        >
          <header hlmCardHeader class="min-w-0 gap-2 border-b border-border px-5 py-4">
            <code class="text-xs text-muted-foreground wrap-anywhere">{{ row.id }}</code>
            <h3 hlmCardTitle [size]="'section'" id="api-detail-title" class="m-0 wrap-anywhere">
              {{ row.name }}
            </h3>
            <p hlmCardDescription class="m-0 text-xs">{{ metadataOnly }}</p>
          </header>
          <div hlmCardContent class="px-5 py-4"><app-api-token-facts [row]="row" /></div>
        </section>
      }
    </div>
  `,
})
export class ApiTokenListComponent {
  protected readonly viewport = inject(NarrowMutationPolicy);

  readonly rows = input<readonly ApiTokenView[]>([]);
  readonly canWrite = input(false);
  readonly busy = input(false);
  readonly revoke = output<ApiTokenView>();
  protected readonly copy = API_TOKENS_COPY;
  protected readonly writeCopy = API_TOKENS_WRITE_COPY;
  protected readonly stateLabel = apiTokenStateLabel;
  protected readonly pageCounts = computed<readonly SettingsPageCount[]>(() => {
    const rows = this.rows();
    let active = 0;
    let expired = 0;
    let revoked = 0;
    for (const row of rows) {
      if (row.status === 'active') active++;
      else if (row.status === 'expired') expired++;
      else if (row.status === 'revoked') revoked++;
    }
    return [
      { label: this.copy.directory, icon: 'key', value: rows.length },
      { label: this.copy.active, icon: 'shield-check', value: active },
      { label: this.copy.expired, icon: 'scroll', value: expired },
      { label: this.copy.revoked, icon: 'shield-alert', value: revoked },
    ];
  });
  protected readonly query = linkedSignal(() => {
    this.rows();
    return '';
  });
  protected readonly filtered = computed(() => {
    const query = this.query().trim().toLocaleLowerCase();
    return this.rows().filter((row) =>
      `${row.name} ${row.prefix}`.toLocaleLowerCase().includes(query),
    );
  });
  protected readonly selectedId = signal<string | null>(null);
  protected readonly selected = computed(
    () => this.filtered().find((row) => row.id === this.selectedId()) ?? this.filtered()[0] ?? null,
  );
  protected readonly searchLabel = $localize`:@@settings.api-tokens.search:Search name or prefix`;
  protected readonly loadedOnly = $localize`:@@settings.api-tokens.loaded-only:Search applies to this loaded page only. Complete project totals are not available.`;
  protected readonly metadataOnly = $localize`:@@settings.api-tokens.metadata-only:Metadata only. Existing token values cannot be retrieved.`;
  protected readonly noMatches = $localize`:@@settings.api-tokens.no-matches:No tokens match this search`;
  protected readonly clearSearch = $localize`:@@settings.api-tokens.clear-search:Clear search`;
  protected search(event: Event): void {
    if (event.target instanceof HTMLInputElement) this.query.set(event.target.value);
  }
  protected inspectLabel(name: string): string {
    return $localize`:@@settings.api-tokens.inspect-named:Inspect token ${name}:name:`;
  }
  protected requestRevoke(row: ApiTokenView): void {
    if (this.viewport.allowsMutation() && this.canWrite() && !this.busy()) this.revoke.emit(row);
  }
}
