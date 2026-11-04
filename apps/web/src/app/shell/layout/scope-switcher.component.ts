import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { OrganizationSummaryDto, ProjectDto } from '@evidgate/sdk';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';

type Scope = 'org' | 'project';

@Component({
  selector: 'app-scope-switcher',
  imports: [HlmLabel, HlmSelectImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './scope-switcher.component.html',
  host: {
    class: 'group/scope grid min-w-0 gap-(--eg-space-3)',
    '[class.compact]': 'compact()',
    '[attr.data-compact]': 'compact()',
  },
})
export class ScopeSwitcherComponent {
  private static nextId = 0;
  readonly orgId = `scope-org-${ScopeSwitcherComponent.nextId++}`;
  readonly projectId = `${this.orgId}-project`;
  readonly compact = input(false);
  readonly organizations = input<readonly OrganizationSummaryDto[]>([]);
  readonly projects = input<readonly ProjectDto[]>([]);
  readonly currentOrg = input<OrganizationSummaryDto | null>(null);
  readonly currentProject = input<ProjectDto | null>(null);
  readonly orgSelected = output<string>();
  readonly projectSelected = output<string>();
  readonly chooseOrganization = $localize`:@@shell.switcher.choose-organization:Choose an organization`;
  readonly chooseProject = $localize`:@@shell.switcher.choose-project:Choose a project`;
  readonly noProjects = $localize`:@@shell.switcher.no-projects:No projects`;

  // Brain commits only activated options; arrow-key exploration does not navigate.
  select(scope: Scope, value: unknown): void {
    if (typeof value !== 'string' || value === '') return;
    if (scope === 'org') {
      if (value === this.currentOrg()?.id) return;
      this.orgSelected.emit(value);
    } else {
      if (value === this.currentProject()?.id) return;
      this.projectSelected.emit(value);
    }
  }
}
