import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/** Identical, input-free copy for unmatched routes and every unresolved organization/project. */
@Component({
  selector: 'app-not-found-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: '../layout/shell-page.css',
  template: `<section>
    <h1>{{ heading }}</h1>
    <p>{{ explanation }}</p>
    <a routerLink="/">{{ action }}</a>
  </section>`,
})
export class NotFoundPage {
  readonly heading = $localize`:@@shell.page.not-found.heading:Page not found`;
  readonly explanation = $localize`:@@shell.page.not-found.explanation:Check the address or return to your workspace.`;
  readonly action = $localize`:@@shell.page.not-found.action:Back to workspace`;
}
