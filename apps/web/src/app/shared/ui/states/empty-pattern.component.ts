import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

@Component({
  selector: 'app-empty-pattern',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['./state-panel.css', './empty-pattern.component.css'],
  template: `
    <section class="state-panel state-panel--empty">
      <h2 class="state-panel__title">{{ title() }}</h2>
      @if (description(); as description) {
        <p class="state-panel__description">{{ description }}</p>
      }
      @if (actionLabel(); as actionLabel) {
        @if (actionHref(); as actionHref) {
          <a class="state-panel__action state-panel__action--primary" [href]="actionHref">
            {{ actionLabel }}
          </a>
        } @else {
          <button
            class="state-panel__action state-panel__action--primary"
            type="button"
            (click)="action.emit()"
          >
            {{ actionLabel }}
          </button>
        }
      }
    </section>
  `,
})
export class EmptyPatternComponent {
  /** Contextual copy is localized by the caller; no domain result is inferred here. */
  readonly title = input.required<string>();
  readonly description = input<string | undefined>();
  readonly actionLabel = input<string | undefined>();
  readonly actionHref = input<string | undefined>();
  readonly action = output<void>();
}
