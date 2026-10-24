import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-forbidden-pattern',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['./state-panel.css', './forbidden-pattern.component.css'],
  template: `
    <section class="state-panel state-panel--forbidden" role="status">
      <h2 class="state-panel__title" i18n="@@shared.states.forbidden.heading">Not authorized</h2>
      <p class="state-panel__description" i18n="@@shared.states.forbidden.description">
        Ask a project administrator for access to this workspace.
      </p>
    </section>
  `,
})
export class ForbiddenPatternComponent {}
