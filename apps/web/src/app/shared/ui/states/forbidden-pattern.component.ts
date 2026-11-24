import { ChangeDetectionStrategy, Component } from '@angular/core';
import { HlmCard, HlmCardDescription, HlmCardTitle } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';

@Component({
  selector: 'app-forbidden-pattern',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmIcon, HlmCard, HlmCardDescription, HlmCardTitle],
  host: { class: 'block min-w-0' },
  template: `
    <section
      hlmCard
      class="state-panel state-panel--forbidden min-w-0 items-start gap-(--eg-space-3) p-(--eg-space-7) text-start wrap-anywhere"
      role="status"
    >
      <hlm-icon [name]="'lucideOctagonX'" class="size-(--eg-space-7) text-(--eg-text-muted)" />
      <h2
        hlmCardTitle
        class="state-panel__title m-0 font-(family-name:--eg-font-display) text-lg"
        [textContent]="heading"
      ></h2>
      <p
        hlmCardDescription
        class="state-panel__description m-0 max-w-prose text-sm"
        i18n="@@shared.states.forbidden.description"
      >
        Ask a project administrator for access to this workspace.
      </p>
    </section>
  `,
})
export class ForbiddenPatternComponent {
  protected readonly heading = $localize`:@@shared.states.forbidden.heading:Not authorized`;
}
