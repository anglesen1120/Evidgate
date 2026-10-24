import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-not-found-pattern',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['./state-panel.css', './not-found-pattern.component.css'],
  template: `
    <section class="state-panel state-panel--not-found">
      <h2 class="state-panel__title" i18n="@@shared.states.not-found.heading">Not found</h2>
      <p class="state-panel__description" i18n="@@shared.states.not-found.description">
        This page does not exist or you do not have access to it.
      </p>
    </section>
  `,
})
export class NotFoundPatternComponent {}
