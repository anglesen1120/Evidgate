// Show only the project resolved by the session scope; never guess a name from the URL.

import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { HlmCard, HlmCardTitle, HlmCardDescription } from '@evidgate/ui/card';
import { HlmButton } from '@evidgate/ui/button';
import { RouterLink } from '@angular/router';

import { SessionStore } from '../../core/session/session.store';
import { NavViewService } from '../../shell/nav/nav-view';

const NEXT_STEP_IDS = ['runs', 'onboarding', 'authoring'];

@Component({
  selector: 'app-project-overview',
  imports: [HlmCard, HlmCardTitle, HlmCardDescription, HlmButton, RouterLink],
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
        [size]="pageHeadingSize"
        class="min-w-0 max-w-full"
        id="project-overview-heading"
        [textContent]="project.name"
      ></h1>
      <p
        hlmCardDescription
        class="slug m-0 min-w-0 max-w-full font-(family-name:--eg-font-mono)"
        [textContent]="project.slug"
      ></p>
      @if (nextSteps().length > 0) {
        <div class="grid min-w-0 gap-(--eg-space-3)">
          <h2
            hlmCardTitle
            [size]="sectionHeadingSize"
            id="project-next-steps"
            i18n="@@shell.overview.next-steps"
          >
            Next steps
          </h2>
          <p hlmCardDescription class="m-0" i18n="@@shell.overview.choose-next-step">
            Choose a next step for this project.
          </p>
          <nav class="flex flex-wrap gap-(--eg-space-3)" aria-labelledby="project-next-steps">
            @for (step of nextSteps(); track step.id) {
              <a
                hlmBtn
                [variant]="$first ? 'default' : 'outline'"
                class="h-auto min-h-(--eg-control-touch) max-w-full whitespace-normal text-center [overflow-wrap:anywhere]"
                [routerLink]="step.link"
                >{{ step.label }}</a
              >
            }
          </nav>
        </div>
      }
    } @else {
      <h1
        hlmCardTitle
        [size]="pageHeadingSize"
        class="min-w-0 max-w-full"
        id="project-overview-heading"
        i18n="@@shell.overview.no-project"
      >
        No project selected
      </h1>
    }
  </section>`,
})
export class ProjectOverviewPage {
  readonly pageHeadingSize = 'page' as const;
  readonly sectionHeadingSize = 'section' as const;
  private readonly session = inject(SessionStore);
  private readonly nav = inject(NavViewService);
  readonly nextSteps = computed(() =>
    this.nav
      .groups()
      .flatMap((group) => group.items)
      .filter((item) => NEXT_STEP_IDS.includes(item.id))
      .sort((left, right) => NEXT_STEP_IDS.indexOf(left.id) - NEXT_STEP_IDS.indexOf(right.id)),
  );

  /** The project the scope guards resolved, or `null` while none is current. */
  readonly project = this.session.currentProject;
}
