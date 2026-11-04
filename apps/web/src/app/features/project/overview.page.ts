// Show only the project resolved by the session scope; never guess a name from the URL.

import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { HlmCard, HlmCardTitle, HlmCardDescription } from '@evidgate/ui/card';

import { SessionStore } from '../../core/session/session.store';

@Component({
  selector: 'app-project-overview',
  imports: [HlmCard, HlmCardTitle, HlmCardDescription],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 font-sans text-foreground' },
  template: `<section
    hlmCard
    class="min-w-0 items-start gap-(--eg-space-4) px-(--eg-space-4) py-(--eg-space-8) wrap-anywhere min-[768px]:px-(--eg-space-5)"
    aria-labelledby="project-overview-heading"
  >
    @if (project(); as project) {
      <h1
        hlmCardTitle
        class="m-0 min-w-0 max-w-full font-(family-name:--eg-font-display) text-xl font-(weight:--eg-weight-bold)"
        id="project-overview-heading"
        [textContent]="project.name"
      ></h1>
      <p
        hlmCardDescription
        class="slug m-0 min-w-0 max-w-full font-(family-name:--eg-font-mono)"
        [textContent]="project.slug"
      ></p>
      <p hlmCardDescription class="m-0" i18n="@@shell.overview.arriving">
        This project's runs, authoring and releases arrive with their own stories. For now this page
        shows the project the URL resolved to.
      </p>
    } @else {
      <h1
        hlmCardTitle
        class="m-0 min-w-0 max-w-full font-(family-name:--eg-font-display) text-xl font-(weight:--eg-weight-bold)"
        id="project-overview-heading"
        i18n="@@shell.overview.no-project"
      >
        No project selected
      </h1>
    }
  </section>`,
})
export class ProjectOverviewPage {
  private readonly session = inject(SessionStore);

  /** The project the scope guards resolved, or `null` while none is current. */
  readonly project = this.session.currentProject;
}
