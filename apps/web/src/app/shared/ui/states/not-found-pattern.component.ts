import { ChangeDetectionStrategy, Component } from '@angular/core';
import { HlmCard, HlmCardDescription, HlmCardTitle } from '@evidgate/ui/card';

@Component({
  selector: 'app-not-found-pattern',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmCard, HlmCardDescription, HlmCardTitle],
  host: { class: 'block min-w-0' },
  template: `
    <section
      hlmCard
      class="state-panel state-panel--not-found min-w-0 min-h-[calc(var(--eg-space-12)*3+var(--eg-space-8))] items-center justify-center gap-(--eg-space-3) px-(--eg-space-5) py-(--eg-space-8) text-center wrap-anywhere"
      role="status"
    >
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
