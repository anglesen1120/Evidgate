import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * A polite status region that stays mounted. Assistive technology announces a live region only
 * when a region already in the accessibility tree changes, so a notice inserted together with its
 * message can go unspoken; callers mount this once and replace `text` instead.
 */
@Component({
  selector: 'app-live-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<p class="sr-only" role="status" aria-live="polite" aria-atomic="true">
    {{ text() }}
  </p>`,
})
export class LiveStatusComponent {
  /** Caller-localized message; empty text clears the region without removing it. */
  readonly text = input<string>('');
}
