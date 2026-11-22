import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import {
  HlmCard,
  HlmCardContent,
  HlmCardDescription,
  HlmCardHeader,
  HlmCardTitle,
} from '@evidgate/ui/card';

@Component({
  selector: 'app-no-organization-page',
  imports: [
    RouterLink,
    HlmButton,
    HlmCard,
    HlmCardContent,
    HlmCardDescription,
    HlmCardHeader,
    HlmCardTitle,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 font-sans text-foreground' },
  template: `<section
    hlmCard
    class="min-w-0 gap-(--eg-space-5) py-(--eg-space-6)"
    aria-labelledby="no-organization-heading"
  >
    <header hlmCardHeader>
      <h1
        hlmCardTitle
        [size]="pageHeadingSize"
        id="no-organization-heading"
        [textContent]="heading"
      ></h1>
      <p hlmCardDescription class="max-w-prose text-sm">{{ explanation }}</p>
    </header>
    <div hlmCardContent>
      <a
        hlmBtn
        [variant]="'outline'"
        class="h-auto min-h-(--eg-control-touch) max-w-full whitespace-normal"
        routerLink="/"
        >{{ action }}</a
      >
    </div>
  </section>`,
})
export class NoOrganizationPage {
  readonly pageHeadingSize = 'page' as const;
  readonly heading = $localize`:@@shell.page.no-organization.heading:No organization yet`;
  readonly explanation = $localize`:@@shell.page.no-organization.explanation:Organization setup will be available with onboarding.`;
  readonly action = $localize`:@@shell.page.no-organization.action:Back to workspace`;
}
