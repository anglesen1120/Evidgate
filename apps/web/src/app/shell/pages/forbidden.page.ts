import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ForbiddenPatternComponent } from '../../shared/ui/states';

/** Route structure only; the shared pattern owns visible refusal copy and presentation. */
@Component({
  selector: 'app-forbidden-page',
  imports: [ForbiddenPatternComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <h1 class="sr-only" i18n="@@shared.states.forbidden.heading">Not authorized</h1>
    <app-forbidden-pattern />
  `,
})
export class ForbiddenPage {}
