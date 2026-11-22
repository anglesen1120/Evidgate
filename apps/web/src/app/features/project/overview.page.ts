// Show only the project resolved by the session scope; never guess a name from the URL.

import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { HlmCard } from '@evidgate/ui/card';
import { HlmButton } from '@evidgate/ui/button';
import { RouterLink } from '@angular/router';

import { SessionStore } from '../../core/session/session.store';
import { PageHeaderImports, SectionHeaderComponent } from '../../shared/ui/page-header';
import { EmptyPatternComponent } from '../../shared/ui/states/empty-pattern.component';
import { ShellIconComponent } from '../../shell/layout/shell-icon.component';
import { NavViewService } from '../../shell/nav/nav-view';

const NEXT_STEP_IDS = ['runs', 'onboarding', 'authoring'];

@Component({
  selector: 'app-project-overview',
  imports: [
    HlmCard,
    HlmButton,
    RouterLink,
    PageHeaderImports,
    SectionHeaderComponent,
    EmptyPatternComponent,
    ShellIconComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 font-sans text-foreground' },
  template: `
    @let current = project();
    <section
      class="grid min-w-0 gap-(--eg-space-7) wrap-anywhere"
      aria-labelledby="project-overview-heading"
    >
      <div class="flex min-w-0 items-start gap-(--eg-space-4)">
        <span
          aria-hidden="true"
          class="grid size-(--eg-space-9) shrink-0 place-items-center rounded-(--eg-radius-md) border border-border bg-(--eg-accent-soft) text-primary md:size-(--eg-space-10)"
        >
          <app-shell-icon class="size-(--eg-space-7)" [name]="'folder'" />
        </span>
        <app-page-header
          class="min-w-0 flex-1"
          [title]="current?.name ?? overviewTitle"
          [titleId]="'project-overview-heading'"
          [description]="projectContext()"
        >
          @if (current) {
            <span pageHeaderContext class="text-xs font-semibold text-muted-foreground">
              {{ overviewTitle }}
            </span>
          }
          @if (current && nextSteps()[0]; as primary) {
            <a appPageHeaderActions hlmBtn class="eg-header-action" [routerLink]="primary.link">
              <app-shell-icon [name]="primary.icon" />{{ primary.label }}
            </a>
          }
        </app-page-header>
      </div>
      @if (!current) {
        <app-empty-pattern [title]="noProject" />
      } @else if (!nextSteps().length) {
        <app-empty-pattern
          title="No shortcuts available for this project"
          i18n-title="@@shell.overview.no-shortcuts"
        />
      } @else if (shortcuts().length) {
        <nav class="grid min-w-0 gap-(--eg-space-5)" aria-labelledby="project-next-steps">
          <app-section-header
            [titleId]="'project-next-steps'"
            title="Next steps"
            i18n-title="@@shell.overview.next-steps"
            description="Choose a next step for this project."
            i18n-description="@@shell.overview.choose-next-step"
          />
          <ul
            class="m-0 grid min-w-0 list-none gap-(--eg-space-5) p-0 min-[941px]:grid-cols-[repeat(auto-fit,minmax(min(18rem,100%),1fr))]"
          >
            @for (step of shortcuts(); track step.id) {
              <li
                hlmCard
                class="min-w-0 justify-between gap-(--eg-space-4) rounded-(--eg-radius-lg) p-(--eg-space-5)"
              >
                @switch (step.id) {
                  @case ('onboarding') {
                    <p
                      class="m-0 text-sm text-muted-foreground"
                      i18n="@@shell.overview.setup-guide"
                    >
                      Follow the workspace setup steps.
                    </p>
                  }
                  @case ('authoring') {
                    <p
                      class="m-0 text-sm text-muted-foreground"
                      i18n="@@shell.overview.authoring-guide"
                    >
                      Browse features and their test cases.
                    </p>
                  }
                }
                <a
                  hlmBtn
                  [variant]="'outline'"
                  class="eg-header-action w-full"
                  [routerLink]="step.link"
                >
                  <app-shell-icon [name]="step.icon" />{{ step.label }}
                </a>
              </li>
            }
          </ul>
        </nav>
      }
    </section>
  `,
})
export class ProjectOverviewPage {
  private readonly session = inject(SessionStore);

  private readonly nav = inject(NavViewService);

  protected readonly overviewTitle = $localize`:@@shell.overview.title:Project overview`;
  protected readonly noProject = $localize`:@@shell.overview.no-project:No project selected`;
  readonly nextSteps = computed(() =>
    this.nav
      .groups()
      .flatMap((group) => group.items)
      .filter((item) => NEXT_STEP_IDS.includes(item.id))
      .sort((left, right) => NEXT_STEP_IDS.indexOf(left.id) - NEXT_STEP_IDS.indexOf(right.id)),
  );
  protected readonly shortcuts = computed(() => this.nextSteps().slice(1));

  /** The project the scope guards resolved, or `null` while none is current. */
  readonly project = this.session.currentProject;
  protected readonly projectContext = computed(() => {
    const project = this.project();
    if (!project?.case_prefix) return project?.slug;
    return $localize`:@@shell.overview.scope-line:${project.slug}:slug: · Case prefix: ${project.case_prefix}:prefix:`;
  });
}
