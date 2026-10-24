import { ChangeDetectionStrategy, Component } from '@angular/core';
import { NotFoundPatternComponent } from '../../shared/ui/states';

/** Identical, input-free copy for unmatched routes and every unresolved organization/project. */
@Component({
  selector: 'app-not-found-page',
  imports: [NotFoundPatternComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: '../layout/route-pattern-page.css',
  template: `
    <h1 i18n="@@shared.states.not-found.heading">Not found</h1>
    <app-not-found-pattern />
  `,
})
export class NotFoundPage {}
