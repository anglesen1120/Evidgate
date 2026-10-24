import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/** Shell-local convenience pattern; API authorization remains authoritative (T8 owns patterns). */
@Component({
  selector: 'app-forbidden-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: '../layout/shell-page.css',
  template: `<section>
    <h1>{{ heading }}</h1>
    <p>{{ explanation }}</p>
    <a routerLink="/">{{ action }}</a>
  </section>`,
})
export class ForbiddenPage {
  readonly heading = $localize`:@@shell.page.forbidden.heading:You do not have access to this page`;
  readonly explanation = $localize`:@@shell.page.forbidden.explanation:Return to your workspace to continue.`;
  readonly action = $localize`:@@shell.page.forbidden.action:Back to workspace`;
}
