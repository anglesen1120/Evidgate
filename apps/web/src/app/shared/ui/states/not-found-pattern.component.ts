import { ChangeDetectionStrategy, Component } from '@angular/core';
import { HlmCard, HlmCardDescription, HlmCardTitle } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';

@Component({
  selector: 'app-not-found-pattern',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmIcon, HlmCard, HlmCardDescription, HlmCardTitle],
  host: { class: 'block min-w-0' },
  template: `
    <section
      hlmCard
      class="state-panel state-panel--not-found min-w-0 items-start gap-(--eg-space-3) rounded-(--eg-radius-md) p-(--eg-space-5) text-start wrap-anywhere bg-(--eg-surface-raised) text-(--eg-text)"
      role="status"
    >
      <hlm-icon [name]="'lucideInfo'" class="size-(--eg-control-sm) text-(--eg-neutral-fg)" />
      <h2
        hlmCardTitle
        class="state-panel__title m-0 font-(family-name:--eg-font-display) text-lg"
        [textContent]="heading"
      ></h2>
      <p
        hlmCardDescription
        class="state-panel__description m-0 max-w-full"
        i18n="@@shared.states.not-found.description"
      >
        This page does not exist or you do not have access to it.
      </p>
    </section>
  `,
})
export class NotFoundPatternComponent {
  protected readonly heading = $localize`:@@shared.states.not-found.heading:Not found`;
}
