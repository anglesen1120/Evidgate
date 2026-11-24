import { LoadingPatternComponent } from '../../../shared/ui/states/loading-pattern.component';
import { DecimalPipe } from '@angular/common';
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
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmTableImports } from '@evidgate/ui/table';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { DataTableValueComponent } from '../../../shared/ui/data-table/data-table-value.component';
import { RowActionComponent } from '../../../shared/ui/data-table/row-action.component';
import { DesktopMutationDirective } from '../../../shared/ui/narrow/desktop-mutation.directive';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { EmptyPatternComponent } from '../../../shared/ui/states/empty-pattern.component';
import { NumberValueComponent } from '../../../shared/ui/values/number-value.component';
import { rotationReminder } from './rotation-reminder';
import { ROTATION_CLOCK, RotationReminderChipComponent } from './rotation-reminder-chip.component';
import { SecretReferenceFactsComponent } from './secret-reference-facts.component';
import { SECRETS_COPY, secretStatusLabel } from './secrets.copy';

@Component({
  selector: 'app-secret-reference-list',
  imports: [
    LoadingPatternComponent,
    StatusPillComponent,
    RotationReminderChipComponent,
    HlmButton,
    RowActionComponent,
    HlmDialogImports,
    HlmCardImports,
    HlmTableImports,
    NumberValueComponent,
    HlmInput,
    HlmLabel,
    ShellIconComponent,
    DecimalPipe,
    DataTableValueComponent,
    DesktopMutationDirective,
    SecretReferenceFactsComponent,
    EmptyPatternComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 [&_code]:font-mono' },
  templateUrl: './secret-reference-list.component.html',
})
export class SecretReferenceListComponent {
  private readonly viewport = inject(NarrowMutationPolicy);
  private readonly clock = inject(ROTATION_CLOCK);

  readonly fetching = input(false);
  readonly rows = input.required<readonly IngestTokenView[]>();
  readonly canWrite = input(false);
  readonly busy = input(false);
  readonly blocked = input(false);
  readonly busyId = input<string | null>(null);
  readonly rotate = output<{ reference: IngestTokenView; opener: HTMLElement }>();
  readonly filterChange = output<void>();
  protected readonly copy = SECRETS_COPY;
  protected readonly statusLabel = secretStatusLabel;
  protected readonly states = ['active', 'rotated', 'revoked', 'expired'] as const;
  protected readonly query = signal('');
  protected readonly state = signal('');
  protected readonly searchMatches = computed(() => {
    const query = this.query().trim().toLocaleLowerCase();
    return query
      ? this.rows().filter((row) => `${row.name} ${row.prefix}`.toLocaleLowerCase().includes(query))
      : this.rows();
  });
  protected readonly filters = computed(() => {
    const rows = this.searchMatches();
    const counts = new Map<string, number>();
    for (const row of rows) counts.set(row.status, (counts.get(row.status) ?? 0) + 1);
    return [
      { key: '', label: this.copy.allStates, count: rows.length },
      ...this.states.map((key) => ({
        key,
        label: secretStatusLabel(key),
        count: counts.get(key) ?? 0,
      })),
    ];
  });
  protected readonly pageCounts = computed(() => {
    const rows = this.rows();
    const now = this.clock();
    let active = 0;
    let soon = 0;
    let expired = 0;
    for (const row of rows) {
      const reminder = rotationReminder(row, now);
      if (row.status === 'active') active++;
      if (reminder === 'expires-soon') soon++;
      if (row.status === 'expired' || reminder === 'expired') expired++;
    }
    return [
      {
        key: 'references',
        icon: 'folder',
        label: this.copy.references,
        hint: this.copy.pageSummary,
        value: rows.length,
      },
      {
        key: 'active',
        icon: 'tree',
        label: secretStatusLabel('active'),
        hint: this.copy.activeHint,
        value: active,
      },
      {
        key: 'soon',
        icon: 'audit',
        label: this.copy.expiresSoon,
        hint: this.copy.soonHint,
        value: soon,
      },
      {
        key: 'expired',
        icon: 'inbox',
        label: this.copy.expired,
        hint: this.copy.expiredHint,
        value: expired,
      },
    ] as const;
  });
  protected readonly filtered = computed(() => {
    const state = this.state();
    return state
      ? this.searchMatches().filter((row) => row.status === state)
      : this.searchMatches();
  });
  protected search(event: Event): void {
    if (event.target instanceof HTMLInputElement) {
      this.query.set(event.target.value);
      this.filterChange.emit();
    }
  }
  protected selectState(state: string): void {
    this.state.set(state);
    this.filterChange.emit();
  }
  protected clearFilters(): void {
    this.query.set('');
    this.state.set('');
    this.filterChange.emit();
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
