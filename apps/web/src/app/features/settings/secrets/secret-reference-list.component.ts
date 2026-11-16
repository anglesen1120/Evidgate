import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import type { IngestTokenView } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { DataTableValueComponent } from '../../../shared/ui/data-table/data-table-value.component';
import { DesktopMutationDirective } from '../../../shared/ui/narrow/desktop-mutation.directive';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { EmptyPatternComponent } from '../../../shared/ui/states/empty-pattern.component';
import { RotationReminderChipComponent } from './rotation-reminder-chip.component';
import { SecretReferenceFactsComponent } from './secret-reference-facts.component';
import { SECRETS_COPY, secretStatusLabel } from './secrets.copy';

@Component({
  selector: 'app-secret-reference-list',
  imports: [
    StatusPillComponent,
    RotationReminderChipComponent,
    HlmButton,
    HlmCardImports,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    DataTableValueComponent,
    DesktopMutationDirective,
    SecretReferenceFactsComponent,
    EmptyPatternComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 [&_code]:font-mono' },
  template: `
    <div class="grid min-w-0 gap-[var(--eg-space-7)] xl:grid-cols-2">
      <section hlmCard class="min-w-0" aria-labelledby="secret-list-title">
        <header hlmCardHeader>
          <h3 hlmCardTitle id="secret-list-title">{{ copy.title }}</h3>
          <p hlmCardDescription>{{ copy.loadedOnly }}</p>
        </header>
        <div hlmCardContent class="grid min-w-0 gap-[var(--eg-space-5)]">
          <div class="grid min-w-0 gap-[var(--eg-space-2)]">
            <label hlmLabel for="secret-reference-search">{{ copy.search }}</label>
            <input
              hlmInput
              id="secret-reference-search"
              type="search"
              [value]="query()"
              (input)="search($event)"
            />
            <label hlmLabel for="secret-reference-state">{{ copy.status }}</label>
            <hlm-select
              [value]="state()"
              [itemToString]="stateLabel"
              (valueChange)="filterState($event)"
            >
              <hlm-select-trigger [buttonId]="'secret-reference-state'" class="w-full">
                <hlm-select-value [placeholder]="copy.allStates" />
              </hlm-select-trigger>
              <hlm-select-content *hlmSelectPortal [aria-label]="copy.status">
                <hlm-select-item [value]="''">{{ copy.allStates }}</hlm-select-item>
                @for (status of states; track status) {
                  <hlm-select-item [value]="status">{{ statusLabel(status) }}</hlm-select-item>
                }
              </hlm-select-content>
            </hlm-select>
          </div>
          <ul
            class="m-0 grid min-w-0 list-none gap-[var(--eg-space-4)] p-0"
            [attr.aria-label]="copy.title"
          >
            @for (row of filtered(); track row.id) {
              <li
                hlmCard
                [size]="'sm'"
                class="min-w-0 gap-[var(--eg-space-3)] p-[var(--eg-space-4)]"
                data-secret-reference
                [attr.data-selected]="selected()?.id === row.id"
              >
                <div class="flex min-w-0 flex-wrap items-center gap-[var(--eg-space-3)]">
                  <h3 class="m-0 min-w-0 flex-1 text-sm">
                    <app-data-table-value [value]="row.name" [truncate]="true" />
                  </h3>
                  <app-status-pill
                    [tone]="
                      row.status === 'active'
                        ? 'success'
                        : row.status === 'expired'
                          ? 'danger'
                          : 'neutral'
                    "
                    [label]="statusLabel(row.status)"
                  />
                  <app-rotation-reminder-chip [reference]="row" />
                </div>
                <code class="wrap-anywhere">{{ row.prefix }}</code>
                <div class="flex flex-wrap gap-[var(--eg-space-3)]">
                  <button
                    hlmBtn
                    [variant]="'outline'"
                    type="button"
                    data-secret-inspect
                    [attr.aria-pressed]="selected()?.id === row.id"
                    [attr.aria-label]="inspectLabel(row.name)"
                    (click)="selectedId.set(row.id)"
                  >
                    {{ copy.inspect }}
                  </button>
                  @if (canWrite() && row.status === 'active') {
                    <button
                      *appDesktopMutation
                      hlmBtn
                      [variant]="'ghost'"
                      type="button"
                      class="w-fit self-start"
                      data-rotate
                      [disabled]="busy() || blocked()"
                      [attr.data-reference-id]="row.id"
                      [attr.aria-label]="rotateLabel(row.name)"
                      [attr.aria-busy]="busy() && busyId() === row.id"
                      (click)="requestRotate(row, $event)"
                    >
                      {{ busy() && busyId() === row.id ? copy.submitting : copy.rotate }}
                    </button>
                  }
                </div>
                @if (viewport.narrow()) {
                  <app-secret-reference-facts [reference]="row" />
                }
              </li>
            } @empty {
              <li>
                <app-empty-pattern
                  [bare]="true"
                  [title]="copy.noMatches"
                  [description]="copy.loadedOnly"
                />
                <button hlmBtn [variant]="'outline'" type="button" (click)="clearFilters()">
                  {{ copy.clear }}
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
          aria-labelledby="secret-detail-title"
          data-secret-detail
        >
          <header hlmCardHeader>
            <h3 hlmCardTitle id="secret-detail-title" class="wrap-anywhere">{{ row.name }}</h3>
            <p hlmCardDescription>{{ copy.lifecycle }}</p>
          </header>
          <div hlmCardContent class="grid min-w-0 gap-[var(--eg-space-5)]">
            <app-secret-reference-facts [reference]="row" />
            <p class="m-0 text-muted-foreground">{{ copy.metadata }}</p>
          </div>
        </section>
      }
    </div>
  `,
})
export class SecretReferenceListComponent {
  readonly rows = input.required<readonly IngestTokenView[]>();
  readonly canWrite = input(false);
  readonly busy = input(false);
  readonly blocked = input(false);
  readonly busyId = input<string | null>(null);
  readonly rotate = output<{ reference: IngestTokenView; opener: HTMLElement }>();
  protected readonly viewport = inject(NarrowMutationPolicy);
  protected readonly copy = SECRETS_COPY;
  protected readonly statusLabel = secretStatusLabel;
  protected readonly states = ['active', 'rotated', 'revoked', 'expired'] as const;
  protected readonly query = signal('');
  protected readonly state = signal('');
  protected readonly stateLabel = (value: unknown): string =>
    typeof value === 'string' && value
      ? secretStatusLabel(value as IngestTokenView['status'])
      : this.copy.allStates;
  protected readonly selectedId = signal<string | null>(null);
  protected readonly filtered = computed(() => {
    const query = this.query().trim().toLocaleLowerCase();
    return this.rows().filter(
      (row) =>
        (!this.state() || row.status === this.state()) &&
        `${row.name} ${row.prefix}`.toLocaleLowerCase().includes(query),
    );
  });
  protected readonly selected = computed(
    () => this.filtered().find((row) => row.id === this.selectedId()) ?? this.filtered()[0] ?? null,
  );
  protected search(event: Event): void {
    if (event.target instanceof HTMLInputElement) this.query.set(event.target.value);
  }
  protected filterState(value: unknown): void {
    if (typeof value === 'string' && (!value || this.states.some((state) => state === value)))
      this.state.set(value);
  }
  protected clearFilters(): void {
    this.query.set('');
    this.state.set('');
  }
  protected inspectLabel(name: string): string {
    return $localize`:@@settings.secrets.inspect-named:Inspect reference ${name}:name:`;
  }
  protected rotateLabel(name: string): string {
    return $localize`:@@settings.secrets.rotate-named:Rotate credential ${name}:name:`;
  }
  protected requestRotate(reference: IngestTokenView, event: Event): void {
    if (
      this.viewport.allowsMutation() &&
      this.canWrite() &&
      !this.busy() &&
      !this.blocked() &&
      reference.status === 'active' &&
      event.currentTarget instanceof HTMLElement
    )
      this.rotate.emit({ reference, opener: event.currentTarget });
  }
}
