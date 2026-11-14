import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle, HlmCardDescription } from '@evidgate/ui/card';

@Component({
  selector: 'app-no-organization-page',
  imports: [RouterLink, HlmButton, HlmCard, HlmCardTitle, HlmCardDescription],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 font-sans text-foreground' },
  template: `<section
    hlmCard
    class="min-w-0 items-start gap-(--eg-space-4) px-(--eg-space-4) py-(--eg-space-8) [overflow-wrap:anywhere] min-[768px]:px-(--eg-space-5)"
    aria-labelledby="no-organization-heading"
  >
    <h1
      hlmCardTitle
      [size]="pageHeadingSize"
      id="no-organization-heading"
      [textContent]="heading"
    ></h1>
    <p hlmCardDescription class="m-0">{{ explanation }}</p>
    <a
      hlmBtn
      class="h-auto min-h-(--eg-control-touch) max-w-full whitespace-normal"
      routerLink="/"
      >{{ action }}</a
    >
  </section>`,
})
export class NoOrganizationPage {
  readonly pageHeadingSize = 'page' as const;
  readonly heading = $localize`:@@shell.page.no-organization.heading:No organization yet`;
  readonly explanation = $localize`:@@shell.page.no-organization.explanation:Organization setup will be available with onboarding.`;
  readonly action = $localize`:@@shell.page.no-organization.action:Back to workspace`;
}
