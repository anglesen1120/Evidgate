import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-no-organization-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: '../layout/shell-page.css',
  template: `<section>
    <h1>{{ heading }}</h1>
    <p>{{ explanation }}</p>
    <a routerLink="/">{{ action }}</a>
  </section>`,
})
export class NoOrganizationPage {
  readonly heading = $localize`:@@shell.page.no-organization.heading:No organization yet`;
  readonly explanation = $localize`:@@shell.page.no-organization.explanation:Organization setup will be available with onboarding.`;
  readonly action = $localize`:@@shell.page.no-organization.action:Back to workspace`;
}
