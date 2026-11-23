import { formatDate, formatNumber } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, LOCALE_ID } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { SessionStore } from '../../core/session/session.store';
import { PageHeaderImports } from '../../shared/ui/page-header';
import { EmptyPatternComponent } from '../../shared/ui/states/empty-pattern.component';
import { ShellIconComponent } from '../../shell/layout/shell-icon.component';
import { NavViewService } from '../../shell/nav/nav-view';

@Component({
  selector: 'app-project-overview',
  imports: [
    HlmButton,
    RouterLink,
    PageHeaderImports,
    EmptyPatternComponent,
    ShellIconComponent,
  ],
  providers: [ProjectDashboardStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 font-sans text-foreground' },
  templateUrl: './overview.page.html',
  styleUrl: './overview.page.css',
})
export class ProjectOverviewPage {
  protected readonly store = inject(ProjectDashboardStore);
  private readonly session = inject(SessionStore);
  private readonly locale = inject(LOCALE_ID);
  private readonly nav = inject(NavViewService);
  protected readonly copy = DASHBOARD_COPY;
  readonly project = this.session.currentProject;
  protected readonly projectContext = computed(() => {
    const project = this.project();
    if (!project?.case_prefix) return project?.slug;
    return $localize`:@@shell.overview.scope-line:${project.slug}:slug: · Case prefix: ${project.case_prefix}:prefix:`;
  });
}
