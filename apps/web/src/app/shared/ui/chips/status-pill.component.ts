import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export type StatusTone = 'success' | 'info' | 'warning' | 'danger' | 'neutral';

/** Generic presentation only: the caller supplies a localized label and dimension. */
@Component({
  selector: 'app-status-pill',
  template: `
    <span
      class="status-pill"
      [class.status-pill--success]="hasLabel() && tone() === 'success'"
      [class.status-pill--info]="hasLabel() && tone() === 'info'"
      [class.status-pill--warning]="hasLabel() && tone() === 'warning'"
      [class.status-pill--danger]="hasLabel() && tone() === 'danger'"
      [class.status-pill--neutral]="!hasLabel() || tone() === 'neutral'"
    >
      @if (dimension()) {
        <span class="status-pill__dimension">{{ dimension() }}:</span>
      }
      <span class="status-pill__label">{{ displayLabel() }}</span>
    </span>
  `,
  styleUrl: './status-pill.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StatusPillComponent {
  readonly tone = input.required<StatusTone>();
  readonly label = input.required<string>();
  readonly dimension = input('');

  protected readonly hasLabel = computed(() => Boolean(this.label()?.trim()));
  protected readonly displayLabel = computed(() =>
    this.hasLabel()
      ? this.label()
      : $localize`:@@shared.status-pill.unavailable:Status unavailable`,
  );
}
