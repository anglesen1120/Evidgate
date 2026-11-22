import { formatNumber } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  LOCALE_ID,
  output,
} from '@angular/core';
import type { ApiTokenView } from '@evidgate/sdk';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmTableImports } from '@evidgate/ui/table';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
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
    HlmTableImports,
    ShellIconComponent,
    SettingsPageCountsComponent,
    HlmCardImports,
    HlmButton,
    HlmDialogImports,
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
    <app-settings-page-counts
      class="mb-6"
      [attr.data-token-page-count]="''"
      [metrics]="pageCounts()"
    />
    <section hlmCard class="min-w-0 gap-0 rounded-xl p-0" aria-labelledby="api-directory-title">
      <header
        hlmCardHeader
        class="flex min-w-0 flex-wrap items-center justify-between gap-3 border-b border-border px-5 py-4"
      >
        <h3 hlmCardTitle [size]="'subsection'" id="api-directory-title" class="m-0">
          {{ copy.directory }}
        </h3>
        <div class="grid min-w-0 flex-1 basis-64 gap-1">
          <label hlmLabel for="api-token-search">{{ searchLabel }}</label>
          <input
            class="min-w-0 w-full"
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
        @let narrow = viewport.narrow();
        <div hlmTableContainer>
          <table
            hlmTable
            class="w-full table-fixed border-collapse"
            aria-labelledby="api-directory-title"
          >
            <thead hlmTHead class="bg-(--eg-surface-raised) text-xs [&_th]:h-(--eg-control-md)">
              <tr hlmTr class="max-md:border-0">
                <th hlmTh scope="col" class="max-md:h-0 max-md:p-0">
                  <span class="max-md:sr-only">{{ copy.name }}</span>
                </th>
                @if (!narrow) {
                  <th hlmTh scope="col" class="w-[32%] whitespace-normal">{{ copy.prefix }}</th>
                  <th hlmTh scope="col" class="w-[20%] whitespace-normal">{{ copy.state }}</th>
                }
              </tr>
            </thead>
            <tbody hlmTBody class="md:[&_td]:py-1">
              @for (row of filtered(); track row.id) {
                <tr hlmTr data-api-token-row [attr.data-state]="row.status">
                  <td hlmTd class="whitespace-normal max-md:p-4">
                    <hlm-dialog [aria-modal]="true">
                      <button
                        hlmBtn
                        hlmDialogTrigger
                        [variant]="'link'"
                        class="h-auto max-w-full justify-start px-0 py-1 text-start whitespace-normal pointer-coarse:min-h-(--eg-control-touch)"
                        type="button"
                        data-api-inspect
                        [attr.aria-label]="inspectLabel(row.name)"
                      >
                        <app-shell-icon [name]="'key'" class="size-4" />
                        <bdi class="min-w-0 truncate font-bold">{{ display(row.name) }}</bdi>
                      </button>
                      <hlm-dialog-content *hlmDialogPortal [showCloseButton]="false" [size]="'md'">
                        <header hlmDialogHeader>
                          <h2 hlmDialogTitle>
                            <bdi>{{ display(row.name) }}</bdi>
                          </h2>
                          <code class="text-xs text-muted-foreground"
                            ><bdi>{{ display(row.id) }}</bdi></code
                          >
                          <p hlmDialogDescription>{{ metadataOnly }}</p>
                        </header>
                        <div class="grid content-start gap-3">
                          <app-status-pill
                            [tone]="row.status === 'active' ? 'info' : 'neutral'"
                            [label]="stateLabel(row.status)"
                          />
                          <app-api-token-facts [row]="row" [showIdentity]="false" />
                        </div>
                        <footer hlmDialogFooter>
                          <button
                            hlmBtn
                            hlmDialogClose
                            [variant]="'outline'"
                            type="button"
                            i18n="@@ui.close"
                          >
                            Close
                          </button>
                        </footer>
                      </hlm-dialog-content>
                    </hlm-dialog>
                    <app-data-table-value
                      class="text-xs text-muted-foreground font-mono"
                      [value]="row.id"
                      [truncate]="!narrow"
                    />
                    @if (narrow) {
                      <div class="my-2 flex min-w-0 flex-wrap items-center gap-2">
                        <span class="sr-only">{{ copy.prefix }}:</span>
                        <code class="wrap-anywhere text-xs"
                          ><bdi>{{ display(row.prefix) }}</bdi></code
                        >
                        <app-status-pill
                          [tone]="row.status === 'active' ? 'info' : 'neutral'"
                          [label]="stateLabel(row.status)"
                        />
                      </div>
                      <app-api-token-permission-tags
                        [permissions]="row.permissions"
                        [name]="row.name"
                      />
                    }
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
                  </td>
                  @if (!narrow) {
                    <td hlmTd class="font-mono text-xs whitespace-normal wrap-anywhere">
                      <bdi>{{ row.prefix }}</bdi>
                      <app-api-token-permission-tags
                        class="mt-1 block"
                        [permissions]="row.permissions"
                        [name]="row.name"
                      />
                    </td>
                    <td hlmTd>
                      <app-status-pill
                        [tone]="row.status === 'active' ? 'info' : 'neutral'"
                        [label]="stateLabel(row.status)"
                      />
                    </td>
                  }
                </tr>
              }
            </tbody>
          </table>
        </div>
      } @else {
        <div class="grid justify-items-start gap-3 px-5 py-4">
          <app-empty-pattern [bare]="true" [title]="noMatches" [description]="loadedOnly" />
          <button hlmBtn [variant]="'outline'" type="button" (click)="query.set('')">
            {{ clearSearch }}
          </button>
        </div>
      }
      <footer
        class="flex min-w-0 flex-wrap items-center justify-between gap-2 border-t border-border px-4 py-3"
      >
        <p class="m-0 text-xs tabular-nums text-muted-foreground">{{ pageSummary() }}</p>
        <ng-content />
      </footer>
    </section>
  `,
})
export class ApiTokenListComponent {
  protected readonly viewport = inject(NarrowMutationPolicy);
  private readonly locale = inject(LOCALE_ID);

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
      {
        label: this.copy.directory,
        icon: 'folder',
        value: rows.length,
        hint: $localize`:@@settings.api-tokens.page-prefixes:Prefixes on this page`,
      },
      {
        label: this.copy.active,
        icon: 'shield-check',
        value: active,
        hint: $localize`:@@settings.api-tokens.served-state:Server-reported state`,
      },
      {
        label: this.copy.expired,
        icon: 'clock',
        value: expired,
        hint: $localize`:@@settings.api-tokens.not-usable:Not usable`,
      },
      {
        label: this.copy.revoked,
        icon: 'inbox',
        value: revoked,
        hint: $localize`:@@settings.api-tokens.revocation-recorded:Revocation recorded`,
      },
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
  protected readonly pageSummary = computed(() => {
    const filtered = this.filtered().length;
    const range =
      filtered > 1
        ? `${formatNumber(1, this.locale)}–${formatNumber(filtered, this.locale)}`
        : formatNumber(filtered, this.locale);
    return $localize`:@@settings.api-tokens.showing-page:Showing ${range}:range: of ${formatNumber(this.rows().length, this.locale)}:count: tokens on this page.`;
  });
  protected readonly display = escapeFormatChars;
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
