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
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { HlmTableImports } from '@evidgate/ui/table';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { DataTableValueComponent } from '../../../shared/ui/data-table/data-table-value.component';
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
    StatusPillComponent,
    RotationReminderChipComponent,
    HlmButton,
    HlmCardImports,
    HlmIcon,
    HlmTableImports,
    NumberValueComponent,
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
  templateUrl: './secret-reference-list.component.html',
})
export class SecretReferenceListComponent {
  protected readonly viewport = inject(NarrowMutationPolicy);
  private readonly clock = inject(ROTATION_CLOCK);

  readonly rows = input.required<readonly IngestTokenView[]>();
  readonly canWrite = input(false);
  readonly busy = input(false);
  readonly blocked = input(false);
  readonly busyId = input<string | null>(null);
  readonly rotate = output<{ reference: IngestTokenView; opener: HTMLElement }>();
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
      { key: 'references', icon: 'lucideCopy', label: this.copy.title, value: rows.length },
      { key: 'active', icon: 'lucideActivity', label: secretStatusLabel('active'), value: active },
      { key: 'soon', icon: 'lucideClock', label: this.copy.expiresSoon, value: soon },
      { key: 'expired', icon: 'lucideCircle', label: this.copy.expired, value: expired },
    ] as const;
  });
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
