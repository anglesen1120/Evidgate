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
  host: { class: 'block min-w-0' },
  template: `
    <div class="grid min-w-0 gap-[var(--eg-space-7)] xl:grid-cols-2">
      <section hlmCard class="min-w-0" aria-labelledby="api-directory-title">
        <header hlmCardHeader>
          <h3 hlmCardTitle id="api-directory-title">{{ copy.directory }}</h3>
          <p hlmCardDescription>{{ loadedOnly }}</p>
        </header>
        <div hlmCardContent class="grid min-w-0 gap-[var(--eg-space-5)]">
          <div class="grid min-w-0 gap-[var(--eg-space-2)]">
            <label hlmLabel for="api-token-search">{{ searchLabel }}</label>
            <input
              hlmInput
              id="api-token-search"
              type="search"
              [value]="query()"
              [aria-describedby]="'api-token-search-hint'"
              (input)="search($event)"
            />
            <p id="api-token-search-hint" class="m-0 text-muted-foreground">{{ loadedOnly }}</p>
          </div>
          <ul
            class="m-0 grid min-w-0 list-none gap-[var(--eg-space-4)] p-0"
            [attr.aria-label]="copy.directory"
          >
            @for (row of filtered(); track row.id) {
              <li
                hlmCard
                [size]="'sm'"
                class="min-w-0 gap-[var(--eg-space-3)] p-[var(--eg-space-4)]"
                data-api-token-row
                [attr.data-state]="row.status"
                [attr.data-selected]="selected()?.id === row.id"
              >
                <div class="flex min-w-0 items-center gap-[var(--eg-space-3)]">
                  <app-data-table-value
                    class="min-w-0 flex-1"
                    [value]="row.name"
                    [truncate]="true"
                  />
                  <app-status-pill
                    [tone]="row.status === 'active' ? 'success' : 'neutral'"
                    [label]="stateLabel(row.status)"
                  />
                </div>
                <code class="wrap-anywhere">{{ row.prefix }}</code>
                <app-api-token-permission-tags [permissions]="row.permissions" [name]="row.name" />
                <div class="flex flex-wrap gap-[var(--eg-space-3)]">
                  <button
                    hlmBtn
                    [variant]="'outline'"
                    type="button"
                    data-api-inspect
                    [attr.aria-pressed]="selected()?.id === row.id"
                    [attr.aria-label]="inspectLabel(row.name)"
                    (click)="selectedId.set(row.id)"
                  >
                    {{ inspect }}
                  </button>
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
                </div>
                @if (viewport.narrow()) {
                  <app-api-token-facts [row]="row" />
                }
              </li>
            } @empty {
              <li>
                <app-empty-pattern [bare]="true" [title]="noMatches" [description]="loadedOnly" />
                <button hlmBtn [variant]="'outline'" type="button" (click)="query.set('')">
                  {{ clearSearch }}
                </button>
              </li>
            }
          </ul>
        </div>
      </section>
      @if (selected(); as row) {
        <section
          hlmCard
          class="min-w-0 self-start"
          aria-labelledby="api-detail-title"
          data-api-token-detail
        >
          <header hlmCardHeader>
            <h3 hlmCardTitle id="api-detail-title" class="wrap-anywhere">{{ row.name }}</h3>
            <p hlmCardDescription>{{ metadataOnly }}</p>
          </header>
          <div hlmCardContent><app-api-token-facts [row]="row" /></div>
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
  protected readonly inspect = $localize`:@@settings.api-tokens.inspect:Inspect`;
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
