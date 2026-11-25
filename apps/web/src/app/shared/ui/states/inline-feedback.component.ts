import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { HlmAlertImports } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmIcon } from '@evidgate/ui/icon';

export type InlineFeedbackTone = 'info' | 'warning' | 'destructive' | 'success';

const icons = {
  info: 'lucideInfo',
  warning: 'lucideTriangleAlert',
  destructive: 'lucideOctagonX',
  success: 'lucideCircleCheck',
} as const;

@Component({
  selector: 'app-inline-feedback',
  imports: [HlmAlertImports, HlmButton, HlmIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <div
      hlmAlert
      class="spartan-inline-feedback rounded-(--eg-radius-md) p-(--eg-space-4) wrap-anywhere"
      [variant]="tone() === 'destructive' ? 'destructive' : 'default'"
      [attr.data-tone]="tone()"
      [attr.role]="live() === 'off' ? 'group' : live() === 'assertive' ? 'alert' : 'status'"
      [attr.aria-live]="live() === 'off' ? null : live()"
      aria-atomic="true"
    >
      <hlm-icon [name]="icon()" />
      <p hlmAlertTitle class="m-0">{{ title() }}</p>
      <div hlmAlertDescription class="min-w-0 empty:hidden">
        @if (description(); as description) {
          <p class="m-0">{{ description }}</p>
        }
        <ng-content />
      </div>
      <div hlmAlertAction class="empty:hidden">
        @if (actionLabel(); as label) {
          <button
            hlmBtn
            [variant]="'outline'"
            [size]="'sm'"
            class="h-auto max-w-full whitespace-normal"
            type="button"
            [disabled]="actionDisabled()"
            (click)="requestAction()"
          >
            {{ label }}
          </button>
        }
        <ng-content select="[feedbackAction]" />
      </div>
    </div>
  `,
})
export class InlineFeedbackComponent {
  readonly tone = input<InlineFeedbackTone>('info');
  /** Callers own localized contextual copy; this component never infers a domain result. */
  readonly title = input.required<string>();
  readonly description = input<string | undefined>();
  readonly actionLabel = input<string | undefined>();
  readonly actionDisabled = input(false);
  readonly action = output<void>();
  /** Static notices can opt out; urgent async failures can explicitly request assertive delivery. */
  readonly announcement = input<'polite' | 'assertive' | 'off'>();

  protected readonly icon = computed(() => icons[this.tone()]);
  protected readonly live = computed(
    () => this.announcement() ?? (this.tone() === 'destructive' ? 'assertive' : 'polite'),
  );

  protected requestAction(): void {
    if (!this.actionDisabled()) this.action.emit();
  }
}
