import {
  ChangeDetectionStrategy,
  Component,
  computed,
  InjectionToken,
  inject,
  input,
} from '@angular/core';
import type { IngestTokenView } from '@evidgate/sdk';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { rotationReminder } from './rotation-reminder';

/** A clock seam for deterministic expiry boundaries, not a continuously announced countdown. */
export const ROTATION_CLOCK = new InjectionToken<() => Date>('Settings rotation clock', {
  providedIn: 'root',
  factory: () => () => new Date(),
});

@Component({
  selector: 'app-rotation-reminder-chip',
  imports: [StatusPillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (reminder() !== 'none') {
      <span data-rotation-reminder [attr.data-reminder]="reminder()">
        <app-status-pill
          [tone]="reminder() === 'expired' ? 'danger' : 'warning'"
          [label]="reminder() === 'expired' ? expired : soon"
        />
      </span>
    }
  `,
})
export class RotationReminderChipComponent {
  private readonly clock = inject(ROTATION_CLOCK);

  readonly reference = input.required<Pick<IngestTokenView, 'status' | 'expires_at'>>();
  readonly now = input<Date>();
  protected readonly reminder = computed(() =>
    rotationReminder(this.reference(), this.now() ?? this.clock()),
  );
  protected readonly soon = $localize`:@@settings.secrets.expires-soon:Expires soon`;
  protected readonly expired = $localize`:@@settings.secrets.expired:Expired`;
}
