import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardDescription, HlmCardTitle } from '@evidgate/ui/card';

@Component({
  selector: 'app-empty-pattern',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, HlmCard, HlmCardDescription, HlmCardTitle],
  host: { class: 'block min-w-0' },
  template: `
    <section
      hlmCard
      class="state-panel state-panel--empty min-w-0 min-h-[calc(var(--eg-space-12)*3+var(--eg-space-8))] items-center justify-center gap-(--eg-space-3) px-(--eg-space-5) py-(--eg-space-8) text-center wrap-anywhere"
    >
      <h2
        hlmCardTitle
        class="state-panel__title m-0 font-(family-name:--eg-font-display) text-lg"
        [textContent]="title()"
      ></h2>
      @if (description(); as description) {
        <p hlmCardDescription class="state-panel__description m-0 max-w-full">{{ description }}</p>
      }
      @if (actionLabel(); as actionLabel) {
        @if (actionHref(); as actionHref) {
          <a
            hlmBtn
            class="state-panel__action state-panel__action--primary h-auto min-h-(--eg-control-md) max-w-full whitespace-normal py-(--eg-space-2) pointer-coarse:min-h-(--eg-control-touch) pointer-coarse:min-w-(--eg-control-touch)"
            [href]="actionHref"
          >
            {{ actionLabel }}
          </a>
        } @else {
          <button
            hlmBtn
            class="state-panel__action state-panel__action--primary h-auto min-h-(--eg-control-md) max-w-full whitespace-normal py-(--eg-space-2) pointer-coarse:min-h-(--eg-control-touch) pointer-coarse:min-w-(--eg-control-touch)"
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
