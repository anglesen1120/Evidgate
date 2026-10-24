import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { OrganizationSummaryDto, ProjectDto } from '@evidgate/sdk';

/** Render-only scope controls. The router and its guards remain the source of truth. */
@Component({
  selector: 'app-scope-switcher',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './scope-switcher.component.html',
  styleUrl: './scope-switcher.component.css',
})
export class ScopeSwitcherComponent {
  readonly organizations = input<readonly OrganizationSummaryDto[]>([]);
  readonly projects = input<readonly ProjectDto[]>([]);
  readonly currentOrg = input<OrganizationSummaryDto | null>(null);
  readonly currentProject = input<ProjectDto | null>(null);
  readonly orgSelected = output<string>();
  readonly projectSelected = output<string>();
}
