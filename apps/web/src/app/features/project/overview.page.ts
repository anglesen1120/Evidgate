// 09.1.1-T4.1 (plan 09.1.1 §10 T4): the project overview entry — the one project-scope screen the
// shell story delivers, because it is what proves the whole chain end to end: the router resolved
// an org and a project from the URL, `SessionStore` loaded the project list through
// `ApiGateway` → the fixture backend → `fixtures/web-api/projects-list/`, and the page renders a
// server-provided name. Nothing here is derived: the name and slug come from `ProjectDto`, and a
// project the store does not hold renders the empty copy instead of a guess.
//
// Everything else on this screen (runs, authoring, releases) arrives with its feature story.

import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

import { SessionStore } from '../../core/session/session.store';

@Component({
  selector: 'app-project-overview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: '../../shell/layout/shell-page.css',
  template: `@if (project(); as project) {
      <section>
        <h1>{{ project.name }}</h1>
        <p class="slug">{{ project.slug }}</p>
        <p i18n="@@shell.overview.arriving">
          This project's runs, authoring and releases arrive with their own stories. For now this
          page shows the project the URL resolved to.
        </p>
      </section>
    } @else {
      <section>
        <h1 i18n="@@shell.overview.no-project">No project selected</h1>
      </section>
    }`,
})
export class ProjectOverviewPage {
  private readonly session = inject(SessionStore);

  /** The project the scope guards resolved, or `null` while none is current. */
  readonly project = this.session.currentProject;
}
