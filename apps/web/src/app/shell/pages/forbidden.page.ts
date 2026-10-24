import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ForbiddenPatternComponent } from '../../shared/ui/states';

/** Route structure only; the shared T8 pattern owns the visible refusal copy and presentation. */
@Component({
  selector: 'app-forbidden-page',
  imports: [ForbiddenPatternComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: '../layout/route-pattern-page.css',
  template: `
    <h1 i18n="@@shared.states.forbidden.heading">Not authorized</h1>
    <app-forbidden-pattern />
  `,
})
export class ForbiddenPage {}
