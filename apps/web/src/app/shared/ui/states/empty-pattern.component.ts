import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmIcon, type HlmIconName } from '@evidgate/ui/icon';
import { HlmCard, HlmCardDescription, HlmCardTitle } from '@evidgate/ui/card';

@Component({
  selector: 'app-empty-pattern',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmIcon, NgTemplateOutlet, HlmButton, HlmCard, HlmCardDescription, HlmCardTitle],
  host: { class: 'block min-w-0' },
  template: `
    @if (bare()) {
      <section
        class="state-panel state-panel--empty state-panel--bare grid min-w-0 justify-items-start gap-(--eg-space-3) wrap-anywhere"
      >
        <ng-container [ngTemplateOutlet]="content" />
      </section>
    } @else {
      <section
        hlmCard
        class="state-panel state-panel--empty min-w-0 items-start gap-(--eg-space-3) p-(--eg-space-7) text-start wrap-anywhere"
      >
        <ng-container [ngTemplateOutlet]="content" />
      </section>
    }
    <ng-template #content>
      <hlm-icon [name]="icon()" class="size-(--eg-space-7) text-(--eg-accent)" />
      <h2
        hlmCardTitle
        class="state-panel__title m-0 font-(family-name:--eg-font-display) text-lg"
        [textContent]="title()"
      ></h2>
      @if (description(); as description) {
        <p
          hlmCardDescription
          class="state-panel__description m-0 max-w-prose text-sm"
          [textContent]="description"
        ></p>
      }
      @if (actionLabel(); as actionLabel) {
        @if (actionHref(); as actionHref) {
          <a
            hlmBtn
            class="state-panel__action state-panel__action--primary h-auto max-w-full whitespace-normal py-(--eg-space-2) pointer-coarse:min-h-(--eg-control-touch) pointer-coarse:min-w-(--eg-control-touch)"
            [href]="actionHref"
          >
            {{ actionLabel }}
          </a>
        } @else {
          <button
            hlmBtn
            class="state-panel__action state-panel__action--primary h-auto max-w-full whitespace-normal py-(--eg-space-2) pointer-coarse:min-h-(--eg-control-touch) pointer-coarse:min-w-(--eg-control-touch)"
            type="button"
            (click)="action.emit()"
          >
            {{ actionLabel }}
          </button>
        }
      }
    </ng-template>
  `,
})
export class EmptyPatternComponent {
  /** Contextual copy is localized by the caller; no domain result is inferred here. */
  readonly title = input.required<string>();
  readonly icon = input<HlmIconName>('lucideInbox');
  readonly description = input<string | undefined>();
  /** The containing card owns its border and inset when this is true. */
  readonly bare = input(false);
  readonly actionLabel = input<string | undefined>();
  readonly actionHref = input<string | undefined>();
  readonly action = output<void>();
}
